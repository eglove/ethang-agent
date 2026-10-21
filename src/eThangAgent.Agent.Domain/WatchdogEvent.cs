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
///     retry attempts are derived by counting RetrySpawned rows per agent.</summary>
public sealed record WatchdogEvent(
    Guid Id,
    AgentId? AgentId,
    WatchdogEventKind Kind,
    string Detail,
    int Attempt,
    double? RssMb,
    DateTimeOffset CreatedAt);
