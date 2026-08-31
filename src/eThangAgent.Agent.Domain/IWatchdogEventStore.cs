using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Persistence seam for watchdog events. Implemented by storage ACLs; the
///     domain and application layers depend only on this interface.</summary>
public interface IWatchdogEventStore
{
  Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default);

  /// <summary>The most recent events, newest first, bounded by limit.</summary>
  Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default);

  /// <summary>How many events of one kind exist for one agent - the retry-attempt ledger.</summary>
  Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default);
}
