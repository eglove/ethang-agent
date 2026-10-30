namespace eThangAgent.AgentDomain;

/// <summary>Base of the child actor event family (source spec Section 7). Metadata only
///     (D5): ids, timestamps, counts, enums, and short labels — never message or report
///     content, which belongs to transcripts. In-process delivery is synchronous
///     in-order fan-out, at-most-once, ephemeral (D1).</summary>
public abstract record ChildEvent(AgentId ChildId, DateTimeOffset At);

/// <summary>The runtime accepted a Start (initial or watchdog retry).</summary>
public sealed record ChildStartedEvent(AgentId ChildId, DateTimeOffset At, AgentId? ParentId, string ModelId, int Attempts)
    : ChildEvent(ChildId, At);

/// <summary>Emitted at the loop's existing beat points; label is a short phase tag
///     ("turn-start" / "iteration" / "tool:NAME"), never content.</summary>
public sealed record ChildProgressEvent(AgentId ChildId, DateTimeOffset At, ChildPhase Phase, string Label)
    : ChildEvent(ChildId, At);

/// <summary>The supervisor's idle timer crossed its threshold.</summary>
public sealed record ChildIdleAlertEvent(AgentId ChildId, DateTimeOffset At, TimeSpan IdleAge, string LastPhase)
    : ChildEvent(ChildId, At);

/// <summary>Budget accumulators crossed a soft threshold (D8: resources, never time).</summary>
public sealed record ChildBudgetAlertEvent(AgentId ChildId, DateTimeOffset At, string BudgetKind,
    double Consumed, double? Ceiling, double BurnRatePerMinute)
    : ChildEvent(ChildId, At);

/// <summary>The run settled terminally; ReportBytes is a size hint, never the report.</summary>
public sealed record ChildSettledEvent(AgentId ChildId, DateTimeOffset At, AgentStatus TerminalStatus,
    AgentFailureReason? Reason, int ReportBytes)
    : ChildEvent(ChildId, At);

/// <summary>A message landed in a mailbox. Size in bytes; content stays out (D5).
///     <para><paramref name="MessageId"/> is the delivered message's identity (issue #92) —
///     the handle the sender's receipt names and the id the drain/remainder facts carry.
///     <see cref="Guid.Empty"/> means the publisher predates the handshake: the ledger
///     records no receipt for it, because a receipt keyed on an unknown id could never be
///     asked about.</para></summary>
public sealed record MessageDeliveredEvent(AgentId ChildId, DateTimeOffset At, string Direction, int Urgency, int Size,
    Guid MessageId = default)
    : ChildEvent(ChildId, At);

/// <summary>The owning loop drained its mailbox at a safe point (W4.4). Content stays out
///     (D5). Mailbox-lifecycle, not progress.
///     <para><paramref name="MessageIds"/> names exactly which messages the drain consumed
///     (issue #92): this is the fact that turns "enqueued" into "received". The count is
///     derived, never carried beside the ids, so the two can never disagree.</para></summary>
public sealed record MailboxDrainedEvent(AgentId ChildId, DateTimeOffset At, IReadOnlyList<Guid> MessageIds)
    : ChildEvent(ChildId, At)
{
  /// <summary>The number of messages taken — derived from the ids, never a second fact.</summary>
  public int Count => MessageIds.Count;
}

/// <summary>The run settled with these messages still queued (issue #92): they were
///     persisted for the next start, so the model never saw them in the run that just
///     ended. This is the fact that stops a settle from reading as "received" — before it,
///     a message left in the mailbox at settle was indistinguishable from a drained one.
///     Content stays out (D5).</summary>
public sealed record MailboxUndeliveredEvent(AgentId ChildId, DateTimeOffset At, IReadOnlyList<Guid> MessageIds)
    : ChildEvent(ChildId, At);

/// <summary>A turn was interrupted by an Urgent message under an audited policy grant (D4-revised).</summary>
public sealed record PreemptedEvent(AgentId ChildId, DateTimeOffset At, string ByWhom, int Urgency)
    : ChildEvent(ChildId, At);

/// <summary>A durable PLAN record changed (issue #8 follow-up): created, a step added/
///     updated/removed, or a status moved. This is what makes the workbench's Plans view
///     INSTANT and what lets a parent agent watch a subagent's plan work with
///     <c>wakeOn=plan</c> - one subscribable for both readers.
///     <para><paramref name="ChildId"/> is the AUTHORING agent: the child whose run wrote
///     the plan, or the root session for a root write. A parent filtering on plan work
///     therefore sees which of its children did it. <paramref name="Change"/> is a short
///     tag (created | steps | status) and <paramref name="Status"/> names the new status
///     for a status change, null otherwise - metadata only (D5), never plan content.</para>
///     <para>Published by the Composition's <c>PlanChangePublisher</c> over the Plan
///     Domain's own events: the Plan Domain cannot reference this assembly, so the
///     translation lives at the composition root (the same shape the state-change
///     adapter uses).</para></summary>
public sealed record PlanChangedEvent(AgentId ChildId, DateTimeOffset At, int PlanId, string Change, string? Status)
    : ChildEvent(ChildId, At);

/// <summary>A durable CHAIN record changed (issue #312): created, a step claimed/settled/
///     reopened, or a status moved (including a PARK, which is the fact a human must see).
///     <para>WHY A SECOND DURABLE-RECORD FACT BESIDE <see cref="PlanChangedEvent"/>: a plan is
///     the design and a chain is its execution, so they are different artifacts with different
///     lifetimes (#312 D5). One event carrying both would force every reader to ask which record
///     it was looking at, and the rail renders them as two views.</para>
///     <para><paramref name="ChildId"/> is the AUTHORING agent, exactly as the plan fact's is.
///     <paramref name="Change"/> is a short tag (created | step | status) and <paramref name="Status"/>
///     names the new chain status for a status move, null otherwise - metadata only (D5).</para>
///     <para>THE PARK REASON AND THE PARKED STEP ARE DELIBERATELY NOT CARRIED. They are on the
///     record, and the record is what a reader renders: duplicating them here would give a view
///     two sources for one sentence, and the one that arrives first would win by accident. The
///     fact says something moved; the store says what it moved to.</para>
///     <para>Published by the Composition's <c>ChainChangePublisher</c> over the Agent Domain's
///     own events - the <see cref="PlanChangedEvent"/> shape, for the same reason: the fact must
///     ride the stream the ChildHost already forwards, or a chain the model writes in the host
///     process would never reach the app.</para></summary>
public sealed record ChainChangedEvent(AgentId ChildId, DateTimeOffset At, int ChainId, string Change, string? Status)
    : ChildEvent(ChildId, At);

/// <summary>A RUNNING child's provider stream went silent past the stall threshold.
///     Informational: the child is still running; consumers (waits, UI) decide.
///     LastActivityKind is "content" or "reasoning" — the last stream fragment type seen.</summary>
public sealed record ChildStreamStalledEvent(AgentId ChildId, DateTimeOffset At, TimeSpan QuietFor, string LastActivityKind)
    : ChildEvent(ChildId, At);

public interface IAgentEventSubscriber
{
  /// <summary>Called synchronously for every published event. Implementations must be
  ///     fast and fault-free: a throwing subscriber is contained and logged, never
  ///     propagated into the child loop.</summary>
  void OnEvent(ChildEvent evt);
}

/// <summary>The runtime's event stream: children publish lifecycle, liveness, progress,
///     and budget events; subscribers observe instead of polling (P4).</summary>
public interface IAgentEvents
{
  /// <summary>Subscribes until the returned lease is disposed. No replay (D1).</summary>
  IDisposable Subscribe(IAgentEventSubscriber subscriber);

  /// <summary>Publishes to every current subscriber in subscription order. Subscriber
  ///     faults are contained at the stream, never surfaced into publishers.</summary>
  void Publish(ChildEvent evt);
}
