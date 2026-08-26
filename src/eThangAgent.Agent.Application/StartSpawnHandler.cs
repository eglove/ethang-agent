using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path.</summary>
public sealed class StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options) : IAgentSpawnCommand
{
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IAgentRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));

  private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
  private readonly ValidModelReferenceSpecification _modelSpec = new();

  public async Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request,
      CancellationToken ct = default)
  {
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

    string? model = request.Model ?? _options.DefaultModel;
    if (string.IsNullOrWhiteSpace(model))
    {
      return Result.Failure<AgentId>(new DomainError("MissingModel",
          "Provide a model reference or configure SubAgent:DefaultModel."));
    }

    AgentRecord record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, model,
        request.Label, request.TaskPrompt, DateTimeOffset.UtcNow);

    Result<string> saved = await _store.SaveAsync(record, ct);
    if (!saved.IsSuccess)
    {
      return Result.Failure<AgentId>(saved.Error!);
    }

    Result<AgentId> started = await _runtime.Start(record, ct);
    return started.IsSuccess
        ? Result.Success<AgentId>(record.Id)
        : Result.Failure<AgentId>(started.Error!);
  }
}
