using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path. When no explicit model is
///     provided and an IModelSelector is available, runs intelligent model selection; falls back
///     to openrouter/auto on any selection failure.</summary>
public sealed class StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options,
    IModelSelector? modelSelector = null) : IAgentSpawnCommand
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IModelSelector? _modelSelector = modelSelector;

  private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
  private readonly ValidModelReferenceSpecification _modelSpec = new();

  private const string FallbackModel = "openrouter/auto";

  public async Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(parent);
    ArgumentNullException.ThrowIfNull(request);
    Violation? violation = _promptSpec.ViolationFor(request) ?? _modelSpec.ViolationFor(request);
    if (violation is not null)
    {
      return Result.Failure<AgentId>(new DomainError("InvalidSpawnRequest", violation.Message));
    }

    if (parent.Depth >= _options.MaxDepth)
    {
      return Result.Failure<AgentId>(new DomainError("DepthExceeded",
          $"agent depth {parent.Depth} is at the limit ({_options.MaxDepth}); children cannot spawn further"));
    }

    string model = await ResolveModelAsync(request, ct).ConfigureAwait(false);

    AgentRecord record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, model,
        request.Label, request.TaskPrompt, DateTimeOffset.UtcNow);

    Result<string> saved = await _store.SaveAsync(record, ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return Result.Failure<AgentId>(saved.Error!);
    }

    Result<AgentId> started = await _runtime.Start(record, ct).ConfigureAwait(false);
    return started.IsSuccess
        ? Result.Success(record.Id)
        : Result.Failure<AgentId>(started.Error!);
  }

  private async Task<string> ResolveModelAsync(SpawnRequest request, CancellationToken ct)
  {
    // 1. Explicit per-spawn model always wins.
    if (!string.IsNullOrWhiteSpace(request.Model))
    {
      return request.Model!;
    }

    // 2. Configured default model wins.
    if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
    {
      return _options.DefaultModel!;
    }

    // 3. Intelligent selection when a selector is available.
    if (_modelSelector is not null)
    {
      Result<ModelSelectionResult> selection = await _modelSelector.SelectAsync(request.TaskPrompt, ct).ConfigureAwait(false);
      if (selection.IsSuccess)
      {
        return selection.Value!.ModelId;
      }
    }

    // 4. Fallback to openrouter/auto.
    return FallbackModel;
  }
}
