namespace eThangAgent.AgentDomain;

/// <summary>One running child's latest progress fact: the phase the loop last
///     announced, the short label it announced with, and when. Metadata only (D5) —
///     the label is a phase tag ("iteration", "tool:read"), never content.</summary>
public sealed record ChildProgressSnapshot(ChildPhase Phase, string Label, DateTimeOffset At);

/// <summary>The session's progress ledger (issue #93): the model-facing read surface
///     for a RUNNING child's mid-run activity.
///
///     <para>The emission side already existed — a child publishes
///     <see cref="ChildProgressEvent"/> at every loop safe point, the stream crosses the
///     wire for remote children (issue #245), and it feeds the watchdog's idle clock and
///     the Desktop's subagent panel. What was missing was a way for the PARENT to read
///     it: without one, a parent learned nothing about a long run until the child
///     settled, and its only observation window (agent.status) reported a bare
///     status=running.</para>
///
///     <para>The ledger is a subscriber, not a store: it keeps ONE latest snapshot per
///     live child in memory (O(running children), no disk writes per tool call) and
///     drops a child's entry when its settle event arrives, so a terminal record never
///     reports stale activity. It deliberately does NOT feed the wait-wake bus: progress
///     beats fire on every tool call, so a wait that woke on them would never rest
///     (pinned by WaitProgressRemovalTests). Reading is pull-only — agent.status asks,
///     nothing is pushed into the parent's context.</para>
///
///     <para>Subscriber faults are contained by the stream itself; this type only reads
///     event fields, so it cannot fault a publisher.</para></summary>
public sealed class ChildProgressLedger : IAgentEventSubscriber
{
  private readonly Lock _gate = new();
  private readonly Dictionary<Guid, ChildProgressSnapshot> _latest = [];

  /// <inheritdoc cref="IAgentEventSubscriber.OnEvent"/>
  public void OnEvent(ChildEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    lock (_gate)
    {
      switch (evt)
      {
        case ChildProgressEvent progress:
          _latest[progress.ChildId.Value] = new ChildProgressSnapshot(progress.Phase, progress.Label, progress.At);
          break;
        case ChildSettledEvent settled:
          // A settled child has no current phase (P2: facts, not derivations) — the
          // terminal record must never inherit the last beat of the run it finished.
          _ = _latest.Remove(settled.ChildId.Value);
          break;
        default:
          break; // only progress and settle are facts this ledger owns
      }
    }
  }

  /// <summary>The child's latest progress, or null when this ledger has none (never
  ///     started here, already settled, or foreign to this session).</summary>
  public ChildProgressSnapshot? Find(AgentId id)
  {
    lock (_gate)
    {
      return _latest.TryGetValue(id.Value, out ChildProgressSnapshot? snapshot) ? snapshot : null;
    }
  }
}
