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
public sealed class SubAgentSpawner(SubAgentServices services, SessionModelPreferences? preferences = null,
    IContextWindowSource? windowSource = null, IContextCompactor? contextCompactor = null)
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

  /// <summary>Window carried when no window source is wired (legacy wiring, tests): the
  ///     child runs unaccounted. Composition always wires a source, so production children
  ///     always carry a real catalog window. A positive sentinel, not zero — ModelConfig
  ///     validation rejects non-positive windows — and utilization against it computes ~0,
  ///     so even an accidentally attached monitor never trips compaction.</summary>
  public const int ChildLegacyWindowFallback = int.MaxValue;

  /// <summary>Prefix of the wrap-up prompt a resumed run receives instead of its original
  ///     task prompt: identifies watchdog restarts in the transcript.</summary>
  public const string WrapUpNudgeSentinel = "[watchdog] You showed no activity for";

  private readonly IModelProviderFactory _factory = services.Factory ?? throw new ArgumentNullException(nameof(services), "Factory must not be null.");
  private readonly IAgentStore _store = services.Store ?? throw new ArgumentNullException(nameof(services), "Store must not be null.");
  private readonly IToolRegistry _tools = services.Tools ?? throw new ArgumentNullException(nameof(services), "Tools must not be null.");
  private readonly ISystemPromptProvider _systemPrompt = services.SystemPrompt ?? throw new ArgumentNullException(nameof(services), "SystemPrompt must not be null.");
  private readonly SessionModelPreferences? _preferences = preferences;
  private readonly IContextWindowSource? _windowSource = windowSource;
  private readonly IContextCompactor? _contextCompactor = contextCompactor;
  private readonly IAgentHeartbeat? _heartbeat = services.Heartbeat;
  private readonly IAgentEvents? _events = services.Events;

  private static readonly AsyncLocal<AgentRecord?> RunningChildCurrent = new();

  /// <summary>The child whose loop is currently executing on this async flow, if any.
  ///     Nested agent.spawn calls must resolve their parent from here rather than
  ///     from the composition root's static root record; compositions wire the
  ///     parent context as <c>() => SubAgentSpawner.RunningChild ?? rootRecord</c>.</summary>
  public static AgentRecord? RunningChild => RunningChildCurrent.Value;

  /// <summary>Runs the child's conversation loop under its timeout budget and persists the terminal
  ///     outcome — Completed with the truncated report, or Failed with its reason — plus the child
  ///     transcript delta. It never saves the initial Running row; that is the spawn command's job.
  ///     A failing terminal write is an infrastructure fault and throws. Resume contract: an
  ///     existing persisted transcript hydrates the conversation and the run receives only the
  ///     watchdog wrap-up nudge instead of the original task prompt; only messages this run adds
  ///     are appended back (never duplicating the seed).</summary>
  public async Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(child);
    // Children inherit the session's runtime preferences (the effort picker): the
    // effort choice is a property of the conversation, not of the root agent. A wired
    // window source must know the child's model — accounting cannot run blind. With no
    // source wired (legacy wiring, tests) the config carries the unbounded legacy
    // sentinel and the child simply runs without accounting.
    int? window = _windowSource is null
        ? ChildLegacyWindowFallback
        : await _windowSource.WindowForAsync(child.ModelUsed, null, ct).ConfigureAwait(false)
          ?? throw new InvalidOperationException(
              $"Child model '{child.ModelUsed}' has no catalog context window; the child run cannot proceed. "
              + "This is a composition wiring fault: every spawnable model must have a known window.");
    ModelConfig config = ModelConfig.Create(
        child.ModelUsed, null, ChildMaxTokens, ChildTemperature, window.Value, _preferences?.ReasoningEffort).Value!;

    // Resume hydration: a persisted transcript means this run restarts a previously
    // interrupted (typically watchdog-retried) child - continue the conversation with
    // only the wrap-up nudge instead of replaying the original task prompt. Mirrors
    // the root-session resume hydration in AgentSessionFactory. seed.Count is the
    // persistence baseline: failure paths persist only what this run adds beyond it.
    Result<IReadOnlyList<Message>> persisted = await _store.GetTranscriptAsync(child.Id, ct).ConfigureAwait(false);
    IReadOnlyList<Message> seed = persisted.IsSuccess ? persisted.Value : [];
    bool resuming = seed.Count > 0;
    Conversation conversation = new(resuming ? seed : null);
    string prompt = resuming
        ? WrapUpNudgeSentinel
            + " your run was restarted by the watchdog after an idle timeout. Continue from where you stopped and wrap up now with your final report."
        : child.TaskPrompt;

    // Each child gets its own accountant: two children must never share totals.
    Agent agent = new(_factory.Create(config), conversation, config, _tools,
        new AgentOptions
        {
          SystemPrompt = _systemPrompt,
          Id = child.Id,
          Depth = child.Depth,
          ContextMonitor = new ContextAccountant(window.Value),
          ContextCompactor = _contextCompactor,
          Heartbeat = _heartbeat,
          Events = _events,
        });
    PublishStarted(child);


    string? report = null;
    AgentFailureReason? failureReason = null;
    AgentRecord? previousChild = RunningChildCurrent.Value;
    RunningChildCurrent.Value = child;
    try
    {
      Result<string> run = await agent.SendMessage(prompt,
          inbox: null, ct: ct).ConfigureAwait(false);
      if (run.IsSuccess)
      {
        report = run.Value;
      }
      else
      {
        failureReason = ClassifyRunFailure(ct);
      }
    }
    catch (OperationCanceledException)
    {
      // Only the caller's token can cancel a run now (FR-L4): an explicit interrupt.
      failureReason = AgentFailureReason.Interrupted;
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
      _heartbeat?.Forget(child.Id);
    }

    if (failureReason is not null)
    {
      // Persist the partial transcript delta beyond the hydrated baseline so a later
      // resume continues from the real frontier - and never re-appends earlier rows.
      IReadOnlyList<Message> partial = agent.Conversation.Messages;
      for (int i = seed.Count; i < partial.Count; i++)
      {
        _ = await _store.AppendMessageAsync(child.Id, partial[i], ct).ConfigureAwait(false);
      }

      await PersistTerminalAsync(child with
      {
        Status = AgentStatus.Failed,
        FailureReason = failureReason,
        CompletedAt = DateTimeOffset.UtcNow,
      }, ct).ConfigureAwait(false);
      PublishSettled(child.Id, AgentStatus.Failed, failureReason, 0);
      return new AgentRunOutcome(child.Id, AgentStatus.Failed, failureReason,
          FailureDetail(failureReason.Value), child.ModelUsed, child.Depth);
    }

    string finalReport = report!;
    if (Encoding.UTF8.GetByteCount(finalReport) > MaxReportBytes)
    {
      finalReport += "\n" + ReportOverflowAnnotation;
    }

    // Persist only what this run added: a resumed run's seed already sits in the store,
    // and re-appending it would duplicate the transcript.
    IReadOnlyList<Message> added = agent.Conversation.Messages;
    for (int i = seed.Count; i < added.Count; i++)
    {
      _ = await _store.AppendMessageAsync(child.Id, added[i], ct).ConfigureAwait(false);
    }

    await PersistTerminalAsync(child with
    {
      Status = AgentStatus.Completed,
      CompletedAt = DateTimeOffset.UtcNow,
      FinalReport = finalReport,
    }, ct).ConfigureAwait(false);

    PublishSettled(child.Id, AgentStatus.Completed, null, Encoding.UTF8.GetByteCount(finalReport));
    return new AgentRunOutcome(child.Id, AgentStatus.Completed, null, finalReport,
        child.ModelUsed, child.Depth);
  }

  /// <summary>Emits ChildStartedEvent when a stream is wired; no-op otherwise (legacy wiring).</summary>
  private void PublishStarted(AgentRecord child)
      => _events?.Publish(new ChildStartedEvent(
          child.Id, DateTimeOffset.UtcNow, child.ParentId, child.ModelUsed, child.Attempts));

  /// <summary>Emits ChildSettledEvent; ReportBytes is a size hint only, never content (D5).</summary>
  private void PublishSettled(AgentId id, AgentStatus status, AgentFailureReason? reason, int reportBytes)
      => _events?.Publish(new ChildSettledEvent(id, DateTimeOffset.UtcNow, status, reason, reportBytes));
  private async Task PersistTerminalAsync(AgentRecord terminal, CancellationToken ct)
  {
    Result<string> update = await _store.UpdateAsync(terminal, ct).ConfigureAwait(false);
    if (!update.IsSuccess)
    {
      throw new InvalidOperationException(
          $"failed to persist terminal state for agent '{terminal.Id}': " +
          $"[{update.Error.Code}] {update.Error.Message}");
    }
  }

  /// <summary>Guard-style early returns: the caller's token firing is an explicit user
  /// interrupt, this run's own timeout budget expiring is a timeout, anything else is a
  /// provider failure.</summary>
  /// <summary>The caller's token firing is an explicit interrupt (FR-L4: wall-clock is
  ///     never a cancellation source); anything else reaching here is a provider fault.</summary>
  private static AgentFailureReason ClassifyRunFailure(CancellationToken callerToken)
      => callerToken.IsCancellationRequested
          ? AgentFailureReason.Interrupted
          : AgentFailureReason.ProviderError;
  private static string FailureDetail(AgentFailureReason reason) => reason switch
  {
    AgentFailureReason.Timeout => "child agent timed out before completing.",
    AgentFailureReason.MaxIterations => "child agent hit the tool-iteration limit without a final report.",
    AgentFailureReason.Interrupted => "child agent was interrupted by the user before completing.",
    AgentFailureReason.ProviderError => "child agent's model provider failed.",
    AgentFailureReason.Hung => "child agent was terminated by the watchdog after idle detection and a wrap-up retry.",
    AgentFailureReason.BudgetExhausted => "child agent reached a budget hard ceiling and was terminated.",
    // Unnamed enum values cannot occur.
    _ => "child agent's model provider failed.",
  };
}
