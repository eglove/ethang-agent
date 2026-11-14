namespace eThangAgent.AgentDomain;

public enum WatchdogEventKind
{
  HungDetected,
  RetrySpawned,
  RetryDeferred,
  TerminalReport,
  RssBreached,
  RssSustained,
  WatchdogErrored,
  GrantViolation,
  StreamStalled,

  /// <summary>A spawn-boundary invariant refused a begin-run (issue #124: an unbound
  /// provider record would otherwise loop without a model binding). Append-only audit
  /// only — never a state source.</summary>
  SpawnInvariantViolation,

  /// <summary>The resource governor entered its notice level for a conversation
  /// (spec D2/D4): pressure is real but nothing was enacted yet.</summary>
  ResourceNotice,

  /// <summary>The governor enacted one ladder rung (spec D3/D4).</summary>
  ResourceActed,

  /// <summary>The governor reached the emergency level - usage near the budget AND
  /// still rising (spec D2).</summary>
  ResourceEmergency,

  /// <summary>Usage fell back under the notice level, ending the episode (spec D4).</summary>
  ResourceRecovered,
}

/// <summary>One structured watchdog or process-RSS-monitor decision or observation. Rows are append-only audit:
///     retry attempts are derived by counting RetrySpawned rows per agent.
///     <para>AN EVENT REPEATED IS ONE ROW (issue #386): <see cref="Occurrences"/> says how many
///     times the identical (agent, kind, detail) was recorded inside the store's coalescing
///     window, and <see cref="LastSeenAt"/> is the most recent of them. A freshly inserted row
///     reads 1 and its own creation time, which is what a row written before the count existed
///     reads too.</para>
///     <para>RETRY ATTEMPTS ARE NOT AFFECTED: they are counted per agent and kind, so a
///     RetrySpawned event whose detail repeats still contributes its count rather than one.</para>
///     </summary>
/// <param name="Id">The row's identity. Stable across coalescing: a repeat advances the row it
///     matched rather than minting another.</param>
/// <param name="AgentId">The agent the event is about, or null for a process-scope event.</param>
/// <param name="Kind">Which watchdog decision or observation this is.</param>
/// <param name="Detail">The event's one-line detail, which is what coalescing matches on.</param>
/// <param name="Attempt">The attempt number in force when it was recorded.</param>
/// <param name="RssMb">The working set in MB for an RSS event, or null.</param>
/// <param name="CreatedAt">When the row was first created.</param>
/// <param name="Occurrences">How many identical events this row stands for.</param>
/// <param name="LastSeenAt">When the most recent of them was recorded, or null for a row
///     written before this was tracked, which reads as <paramref name="CreatedAt"/>.</param>
public sealed record WatchdogEvent(
    Guid Id,
    AgentId? AgentId,
    WatchdogEventKind Kind,
    string Detail,
    int Attempt,
    double? RssMb,
    DateTimeOffset CreatedAt,
    int Occurrences = 1,
    DateTimeOffset? LastSeenAt = null)
{
  /// <summary>When this row was last seen, with the single-occurrence rule applied: a row
  ///     written before coalescing existed was seen exactly when it was created.</summary>
  public DateTimeOffset LastSeen => LastSeenAt ?? CreatedAt;
}
