using eThangAgent.AgentDomain;
using eThangAgent.AgentDomain.Specifications;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Start command of the spawn CQRS split: validates the request, persists a Running child,
///     and hands it to the runtime as an independent actor. Owns the validation/depth/model rules
///     that previously lived in SubAgentSpawner's synchronous path.</summary>
public sealed class StartSpawnHandler : IAgentSpawnCommand
{
    private readonly IAgentStore _store;
    private readonly IAgentRuntime _runtime;
    private readonly SubAgentOptions _options;

    private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
    private readonly ValidModelReferenceSpecification _modelSpec = new();

    public StartSpawnHandler(IAgentStore store, IAgentRuntime runtime, SubAgentOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request,
        CancellationToken ct = default)
    {
        var violation = _promptSpec.ViolationFor(request) ?? _modelSpec.ViolationFor(request);
        if (violation is not null)
            return Result<AgentId>.Failure(new Error("InvalidSpawnRequest", violation.Message));

        if (parent.Depth >= _options.MaxDepth)
            return Result<AgentId>.Failure(new Error("DepthExceeded",
                $"agent depth {parent.Depth} is at the limit ({_options.MaxDepth}); children cannot spawn further"));

        var model = request.Model ?? _options.DefaultModel;
        if (string.IsNullOrWhiteSpace(model))
            return Result<AgentId>.Failure(new Error("MissingModel",
                "Provide a model reference or configure SubAgent:DefaultModel."));

        var record = AgentRecord.Spawned(AgentId.NewId(), parent.Id, parent.Depth + 1, model,
            request.Label, request.TaskPrompt, DateTimeOffset.UtcNow);

        var saved = await _store.SaveAsync(record, ct);
        if (!saved.IsSuccess)
            return Result<AgentId>.Failure(saved.Error!);

        var started = await _runtime.Start(record, ct);
        return started.IsSuccess
            ? Result<AgentId>.Success(record.Id)
            : Result<AgentId>.Failure(started.Error!);
    }
}
