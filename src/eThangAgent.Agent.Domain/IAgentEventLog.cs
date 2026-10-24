using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Append-only per-agent event log (the storage ACL's <c>agent_events</c>
///     table). Domain-owned seam, narrow on purpose: the loop records durable facts
///     about a run that are not conversation messages, and never reads them back
///     (issue #263 — the reasoning trace is telemetry, so no context path may reach it).
///     <para>Implementations must never observe the run's cancellation token: the trace
///     of a cancelled turn is exactly the trace worth keeping.</para></summary>
public interface IAgentEventLog
{
  /// <summary>Appends one event row. Best-effort at the caller: a fault here never fails
  ///     the turn it records, and the established persistence discipline applies — a
  ///     failed append is dropped, not retried forever.</summary>
  Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default);
}
