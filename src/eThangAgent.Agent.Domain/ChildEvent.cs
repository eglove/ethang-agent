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

/// <summary>A message landed in a mailbox. Size in bytes; content stays out (D5).</summary>
public sealed record MessageDeliveredEvent(AgentId ChildId, DateTimeOffset At, string Direction, int Urgency, int Size)
    : ChildEvent(ChildId, At);

/// <summary>A turn was interrupted by an Urgent message under an audited policy grant (D4-revised).</summary>
public sealed record PreemptedEvent(AgentId ChildId, DateTimeOffset At, string ByWhom, int Urgency)
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
