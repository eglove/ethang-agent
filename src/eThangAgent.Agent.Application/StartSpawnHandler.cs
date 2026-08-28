using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path. When no explicit model is
///     provided and no session model preference is set, an available IModelSelector runs
///     intelligent model selection; the chain falls back to the host-injected
///     <see cref="SpawnOptions.FallbackModelId"/> on any selection failure.</summary>
public sealed class StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options,
    SpawnOptions spawn, IModelSelector? modelSelector = null)
    : IAgentSpawnCommand
{
  private readonly SpawnOptions _spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IModelSelector? _modelSelector = modelSelector;
  private readonly string _fallbackModelId = spawn?.FallbackModelId
      ?? throw new ArgumentNullException(nameof(spawn));

  private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
  private readonly ValidModelReferenceSpecification _modelSpec = new();

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

    ModelConfig modelConfig = await ResolveModelAsync(request, ct).ConfigureAwait(false);

    AgentRecord record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, modelConfig.ModelId,
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

  private async Task<ModelConfig> ResolveModelAsync(SpawnRequest request, CancellationToken ct)
  {
    // 1. Explicit per-spawn model always wins.
    if (!string.IsNullOrWhiteSpace(request.Model))
    {
      return ModelConfig.Create(request.Model, null, _spawn.MaxTokens, _spawn.Temperature).Value!;
    }

    // 2. The session's live model choice (the host's model picker) is session-wide:
    //    children follow it too, ahead of the static configured default.
    if (!string.IsNullOrWhiteSpace(_spawn.Preferences?.ModelId))
    {
      return ModelConfig.Create(_spawn.Preferences.ModelId, null, _spawn.MaxTokens, _spawn.Temperature).Value!;
    }

    // 3. Configured default model wins.
    if (!string.IsNullOrWhiteSpace(_options.DefaultModel))
    {
      return ModelConfig.Create(_options.DefaultModel, null, _spawn.MaxTokens, _spawn.Temperature).Value!;
    }

    // 4. Intelligent selection when a selector is available.
    if (_modelSelector is not null)
    {
      Result<ModelSelectionResult> selection = await _modelSelector.SelectAsync(request.TaskPrompt, excludedKeys: null, ct).ConfigureAwait(false);
      if (selection.IsSuccess)
      {
        return ModelConfig.Create(selection.Value!.ModelId, selection.Value.ProviderName,
            _spawn.MaxTokens, _spawn.Temperature).Value!;
      }
    }

    // 5. Fallback to the host-injected fallback model.
    return ModelConfig.Create(_fallbackModelId, null, _spawn.MaxTokens, _spawn.Temperature).Value!;
  }
}
