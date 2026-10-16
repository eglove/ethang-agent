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
