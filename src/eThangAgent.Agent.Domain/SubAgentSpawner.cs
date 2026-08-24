using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>Terminal outcome of a child agent run: completion with its report, or failure with its reason.</summary>
public sealed record AgentRunOutcome(
    AgentId ChildId,
    AgentStatus Status,
    AgentFailureReason? Reason,
    string Report,
    string ModelUsed,
    int Depth);

/// <summary>Child-loop runner implementing <see cref="IAgentRunner"/>: executes a persisted child
///     agent's conversation loop to completion and persists its terminal state and transcript.
///     Validation, depth enforcement, model resolution, and the initial Running save belong to the
///     spawn command (<c>StartSpawnHandler</c>), which hands the persisted record here.</summary>
public sealed class SubAgentSpawner : IAgentRunner
{
    /// <summary>Model-facing annotation appended when a child report exceeds the 50 KB storage guideline.</summary>
    public const string ReportOverflowAnnotation =
        "[agent] note: report exceeded 50 KB; flagged for artifact-store overflow.";

    /// <summary>Maximum persisted report size before the overflow annotation is appended.</summary>
    public const int MaxReportBytes = 50 * 1024;

    /// <summary>Child completion budget; matches the composition root's current root-agent settings.</summary>
    public const int ChildMaxTokens = 32 * 1024;
    public const float ChildTemperature = 0.7f;

    private readonly IModelProviderFactory _factory;
    private readonly IAgentStore _store;
    private readonly IToolRegistry _tools;
    private readonly ISystemPromptProvider _systemPrompt;
    private readonly SubAgentOptions _options;

    private static readonly AsyncLocal<AgentRecord?> RunningChildCurrent = new();

    /// <summary>The child whose loop is currently executing on this async flow, if any.
    ///     Nested agent.spawn calls must resolve their parent from here rather than
    ///     from the composition root's static root record; compositions wire the
    ///     parent context as <c>() => SubAgentSpawner.RunningChild ?? rootRecord</c>.</summary>
    public static AgentRecord? RunningChild => RunningChildCurrent.Value;

    public SubAgentSpawner(IModelProviderFactory factory, IAgentStore store, IToolRegistry tools,
        ISystemPromptProvider systemPrompt, SubAgentOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Runs the child's conversation loop under its timeout budget and persists the terminal
    ///     outcome — Completed with the truncated report, or Failed with its reason — plus the child
    ///     transcript. It never saves the initial Running row; that is the spawn command's job. A
    ///     failing terminal write is an infrastructure fault and throws.</summary>
    public async Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
    {
        var config = ModelConfig.Create(child.ModelUsed, ChildMaxTokens, ChildTemperature).Value!;

        var agent = new Agent(_factory.Create(config), new Conversation(), config, _tools,
            _systemPrompt, id: child.Id, depth: child.Depth);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ChildTimeout);

        string? report = null;
        AgentFailureReason? failureReason = null;
        var previousChild = RunningChildCurrent.Value;
        RunningChildCurrent.Value = child;
        try
        {
            var run = await agent.SendMessage(child.TaskPrompt, timeoutCts.Token);
            if (run.IsSuccess)
            {
                report = run.Value!;
            }
            else if (timeoutCts.IsCancellationRequested)
            {
                failureReason = AgentFailureReason.Timeout;
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
            // Provider/loop infrastructure failure: surfaced as Failed(ProviderError) in the
            // outcome so callers persist/retrieve a well-formed error, never a crash.
            failureReason = AgentFailureReason.ProviderError;
        }
        finally
        {
            RunningChildCurrent.Value = previousChild;
        }

        if (failureReason is not null)
        {
            await PersistTerminalAsync(child with
            {
                Status = AgentStatus.Failed,
                FailureReason = failureReason,
                CompletedAt = DateTimeOffset.UtcNow,
            }, ct);
            return new AgentRunOutcome(child.Id, AgentStatus.Failed, failureReason,
                FailureDetail(failureReason.Value), child.ModelUsed, child.Depth);
        }

        var finalReport = report!;
        if (Encoding.UTF8.GetByteCount(finalReport) > MaxReportBytes)
            finalReport += "\n" + ReportOverflowAnnotation;

        foreach (var message in agent.Conversation.Messages)
            await _store.AppendMessageAsync(child.Id, message, ct);

        await PersistTerminalAsync(child with
        {
            Status = AgentStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalReport = finalReport,
        }, ct);

        return new AgentRunOutcome(child.Id, AgentStatus.Completed, null, finalReport,
            child.ModelUsed, child.Depth);
    }

    private async Task PersistTerminalAsync(AgentRecord terminal, CancellationToken ct)
    {
        var update = await _store.UpdateAsync(terminal, ct);
        if (!update.IsSuccess)
            throw new InvalidOperationException(
                $"failed to persist terminal state for agent '{terminal.Id}': " +
                $"[{update.Error!.Code}] {update.Error.Message}");
    }

    private static string FailureDetail(AgentFailureReason reason) => reason switch
    {
        AgentFailureReason.Timeout => "child agent timed out before completing.",
        AgentFailureReason.MaxIterations => "child agent hit the tool-iteration limit without a final report.",
        _ => "child agent's model provider failed.",
    };
}
