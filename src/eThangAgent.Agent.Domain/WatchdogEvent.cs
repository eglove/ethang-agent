namespace eThangAgent.AgentDomain;

public enum WatchdogEventKind
{
  HungDetected,
  RetrySpawned,
  RetryDeferred,
  TerminalReport,
  RssBreached,
  WatchdogErrored,
  GrantViolation,
}

/// <summary>One structured watchdog decision or observation. Rows are append-only audit:
///     retry attempts are derived by counting RetrySpawned rows per agent.</summary>
public sealed record WatchdogEvent(
    Guid Id,
    AgentId? AgentId,
    WatchdogEventKind Kind,
    string Detail,
    int Attempt,
    double? RssMb,
    DateTimeOffset CreatedAt);
