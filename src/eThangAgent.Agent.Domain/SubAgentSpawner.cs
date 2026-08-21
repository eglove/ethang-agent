using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.AgentDomain.Specifications;

namespace eThangAgent.AgentDomain;

/// <summary>Terminal outcome of a successfully completed child agent run.</summary>
public sealed record AgentRunOutcome(
    AgentId ChildId,
    AgentStatus Status,
    AgentFailureReason? Reason,
    string Report,
    string ModelUsed,
    int Depth);

/// <summary>Domain service validating spawn requests, enforcing the depth limit, running the child agent loop, and persisting its lifecycle.</summary>
public sealed class SubAgentSpawner : ISubAgentSpawner
{
    /// <summary>Model-facing annotation appended when a child report exceeds the 50 KB storage guideline.</summary>
    public const string ReportOverflowAnnotation =
        "[agent] note: report exceeded 50 KB; flagged for artifact-store overflow.";

    /// <summary>Maximum persisted report size before the overflow annotation is appended.</summary>
    public const int MaxReportBytes = 50 * 1024;

    /// <summary>Child completion budget; matches the composition root's current root-agent settings.</summary>
    public const int ChildMaxTokens = 1024;
    public const float ChildTemperature = 0.7f;

    private readonly IModelProviderFactory _factory;
    private readonly IAgentStore _store;
    private readonly IToolRegistry _tools;
    private readonly ISystemPromptProvider _systemPrompt;
    private readonly SubAgentOptions _options;

    private readonly NonEmptyTaskPromptSpecification _promptSpec = new();
    private readonly ValidModelReferenceSpecification _modelSpec = new();

    public SubAgentSpawner(IModelProviderFactory factory, IAgentStore store, IToolRegistry tools,
        ISystemPromptProvider systemPrompt, SubAgentOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<Result<AgentRunOutcome>> SpawnAsync(AgentRecord parent, SpawnRequest request,
        CancellationToken ct = default)
    {
        var violation = _promptSpec.ViolationFor(request) ?? _modelSpec.ViolationFor(request);
        if (violation is not null)
            return Result<AgentRunOutcome>.Failure(new Error("InvalidSpawnRequest", violation.Message));

        if (parent.Depth >= _options.MaxDepth)
            return Result<AgentRunOutcome>.Failure(new Error("DepthExceeded",
                $"agent depth {parent.Depth} is at the limit ({_options.MaxDepth}); children cannot spawn further"));

        var model = request.Model ?? _options.DefaultModel;
        if (string.IsNullOrWhiteSpace(model))
            return Result<AgentRunOutcome>.Failure(new Error("MissingModel",
                "supply model or configure SubAgent:DefaultModel"));

        var config = ModelConfig.Create(model, ChildMaxTokens, ChildTemperature).Value!;

        var childId = AgentId.NewId();
        var record = AgentRecord.Spawned(childId, parent.Id, parent.Depth + 1, model,
            request.Label, request.TaskPrompt, DateTimeOffset.UtcNow);

        var saved = await _store.SaveAsync(record, ct);
        if (!saved.IsSuccess)
            return Result<AgentRunOutcome>.Failure(saved.Error!);

        var child = new Agent(_factory.Create(config), new Conversation(), config, _tools,
            _systemPrompt, id: childId, depth: parent.Depth + 1);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ChildTimeout);

        string? report = null;
        AgentFailureReason? failureReason = null;
        try
        {
            var run = await child.SendMessage(request.TaskPrompt, timeoutCts.Token);
            if (run.IsSuccess)
            {
                report = run.Value!;
            }
            else if (timeoutCts.IsCancellationRequested)
            {
                failureReason = AgentFailureReason.Timeout;
            }
            else if (run.Error!.Code == "MaxToolIterations")
            {
                failureReason = AgentFailureReason.MaxIterations;
            }
            else
            {
                failureReason = AgentFailureReason.ProviderError;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout and parent cancellation (Ctrl+C) both record Failed(Timeout), per design.
            failureReason = AgentFailureReason.Timeout;
        }
        catch (Exception)
        {
            // Provider/loop infrastructure failure: persisted as Failed(ProviderError) and returned
            // as a failure Result so the caller receives a well-formed error, never a crash.
            failureReason = AgentFailureReason.ProviderError;
        }

        if (failureReason is not null)
        {
            var failedRecord = record with
            {
                Status = AgentStatus.Failed,
                FailureReason = failureReason,
                CompletedAt = DateTimeOffset.UtcNow,
            };
            var failedUpdate = await _store.UpdateAsync(failedRecord, ct);
            return !failedUpdate.IsSuccess
                ? Result<AgentRunOutcome>.Failure(failedUpdate.Error!)
                : Result<AgentRunOutcome>.Failure(new Error(failureReason.Value.ToString(),
                    FailureDetail(failureReason.Value)));
        }

        var finalReport = report!;
        if (Encoding.UTF8.GetByteCount(finalReport) > MaxReportBytes)
            finalReport += "\n" + ReportOverflowAnnotation;

        foreach (var message in child.Conversation.Messages)
            await _store.AppendMessageAsync(childId, message, ct);

        var completedRecord = record with
        {
            Status = AgentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalReport = finalReport,
        };
        var completedUpdate = await _store.UpdateAsync(completedRecord, ct);
        if (!completedUpdate.IsSuccess)
            return Result<AgentRunOutcome>.Failure(completedUpdate.Error!);

        return Result<AgentRunOutcome>.Success(new AgentRunOutcome(
            childId, AgentStatus.Completed, null, finalReport, model, parent.Depth + 1));
    }

    private static string FailureDetail(AgentFailureReason reason) => reason switch
    {
        AgentFailureReason.Timeout => "child agent timed out before completing.",
        AgentFailureReason.MaxIterations => "child agent hit the tool-iteration limit without a final report.",
        _ => "child agent's model provider failed.",
    };
}
