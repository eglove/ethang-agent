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
public sealed class SubAgentSpawner(IModelProviderFactory factory, IAgentStore store, IToolRegistry tools,
    ISystemPromptProvider systemPrompt, SubAgentOptions options, SessionModelPreferences? preferences = null)
    : IAgentRunner
{
  /// <summary>Model-facing annotation appended when a child report exceeds the 50 KB storage guideline.</summary>
  public const string ReportOverflowAnnotation =
      "[agent] note: report exceeded 50 KB; flagged for artifact-store overflow.";

  /// <summary>Maximum persisted report size before the overflow annotation is appended.</summary>
  public const int MaxReportBytes = 50 * 1024;

  /// <summary>Child completion budget; matches the composition root's current root-agent settings.</summary>
  public const int ChildMaxTokens = 32 * 1024;
  public const float ChildTemperature = 0.7f;

  private readonly IModelProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
  private readonly IAgentStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));
  private readonly ISystemPromptProvider _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
  private readonly SubAgentOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly SessionModelPreferences? _preferences = preferences;

  private static readonly AsyncLocal<AgentRecord?> RunningChildCurrent = new();

  /// <summary>The child whose loop is currently executing on this async flow, if any.
  ///     Nested agent.spawn calls must resolve their parent from here rather than
  ///     from the composition root's static root record; compositions wire the
  ///     parent context as <c>() => SubAgentSpawner.RunningChild ?? rootRecord</c>.</summary>
  public static AgentRecord? RunningChild => RunningChildCurrent.Value;

  /// <summary>Runs the child's conversation loop under its timeout budget and persists the terminal
  ///     outcome — Completed with the truncated report, or Failed with its reason — plus the child
  ///     transcript. It never saves the initial Running row; that is the spawn command's job. A
  ///     failing terminal write is an infrastructure fault and throws.</summary>
  public async Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(child);
    // Children inherit the session's runtime preferences (the effort picker): the
    // effort choice is a property of the conversation, not of the root agent.
    ModelConfig config = ModelConfig.Create(
        child.ModelUsed, null, ChildMaxTokens, ChildTemperature, _preferences?.ReasoningEffort).Value!;

    Agent agent = new(_factory.Create(config), new Conversation(), config, _tools,
        _systemPrompt, id: child.Id, depth: child.Depth);

    using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(_options.ChildTimeout);

    string? report = null;
    AgentFailureReason? failureReason = null;
    AgentRecord? previousChild = RunningChildCurrent.Value;
    RunningChildCurrent.Value = child;
    try
    {
      Result<string> run = await agent.SendMessage(child.TaskPrompt,
          inbox: null, ct: timeoutCts.Token).ConfigureAwait(false);
      if (run.IsSuccess)
      {
        report = run.Value!;
      }
      // The caller's token firing means an explicit interrupt (user stop), which is
      // distinct from this run's own timeout budget expiring.
      else
      {
        failureReason = ClassifyRunFailure(ct, timeoutCts.Token);
      }
    }
    catch (OperationCanceledException)
    {
      failureReason = ct.IsCancellationRequested
          ? AgentFailureReason.Interrupted
          : AgentFailureReason.Timeout;
    }
    // Named decision (CA1031): a child run is an isolation boundary — ANY fault here
    // must become a well-formed terminal outcome, not a crash of the spawning agent.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
    {
      // Provider/loop infrastructure failure: surfaced as Failed(ProviderError) in the
      // outcome so callers persist/retrieve a well-formed error, never a crash.
      failureReason = AgentFailureReason.ProviderError;
    }
#pragma warning restore CA1031
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
      }, ct).ConfigureAwait(false);
      return new AgentRunOutcome(child.Id, AgentStatus.Failed, failureReason,
          FailureDetail(failureReason.Value), child.ModelUsed, child.Depth);
    }

    string finalReport = report!;
    if (Encoding.UTF8.GetByteCount(finalReport) > MaxReportBytes)
    {
      finalReport += "\n" + ReportOverflowAnnotation;
    }

    foreach (Message message in agent.Conversation.Messages)
    {
      _ = await _store.AppendMessageAsync(child.Id, message, ct).ConfigureAwait(false);
    }

    await PersistTerminalAsync(child with
    {
      Status = AgentStatus.Completed,
      CompletedAt = DateTimeOffset.UtcNow,
      FinalReport = finalReport,
    }, ct).ConfigureAwait(false);

    return new AgentRunOutcome(child.Id, AgentStatus.Completed, null, finalReport,
        child.ModelUsed, child.Depth);
  }

  private async Task PersistTerminalAsync(AgentRecord terminal, CancellationToken ct)
  {
    Result<string> update = await _store.UpdateAsync(terminal, ct).ConfigureAwait(false);
    if (!update.IsSuccess)
    {
      throw new InvalidOperationException(
          $"failed to persist terminal state for agent '{terminal.Id}': " +
          $"[{update.Error!.Code}] {update.Error.Message}");
    }
  }

  /// <summary>Guard-style early returns: the caller's token firing is an explicit user
  /// interrupt, this run's own timeout budget expiring is a timeout, anything else is a
  /// provider failure.</summary>
  private static AgentFailureReason ClassifyRunFailure(CancellationToken callerToken, CancellationToken runToken)
  {
    if (callerToken.IsCancellationRequested)
    {
      return AgentFailureReason.Interrupted;
    }

    bool runTimedOut = runToken.IsCancellationRequested;
    return runTimedOut ? AgentFailureReason.Timeout : AgentFailureReason.ProviderError;
  }

  private static string FailureDetail(AgentFailureReason reason) => reason switch
  {
    AgentFailureReason.Timeout => "child agent timed out before completing.",
    AgentFailureReason.MaxIterations => "child agent hit the tool-iteration limit without a final report.",
    AgentFailureReason.Interrupted => "child agent was interrupted by the user before completing.",
    AgentFailureReason.ProviderError => "child agent's model provider failed.",
    // Unnamed enum values cannot occur.
    _ => "child agent's model provider failed.",
  };
}
