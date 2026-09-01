using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Exact orphan resolution at startup (FR-L8, R3.2): a persisted Running
///     record is trusted only when its id is live in an OWNER — this container's
///     in-process runtime (its active map) or the remote host's declared live set.
///     Everything else is Failed(Interrupted) plus one audit row. The former
///     heartbeat-presence heuristic retires with this: presence is now a fact from
///     the owners, never an inference from beat recency. Audit rows record decisions;
///     they are never consulted as state (P2).</summary>
public sealed class OrphanRepairHandler(
    IAgentStore store,
    Func<IReadOnlyCollection<Guid>> inProcessLive,
    Func<IReadOnlyCollection<Guid>> declaredLive,
    IWatchdogEventStore? audit = null)
{
  public async Task RepairAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<AgentRecord>> listed = await store.ListAllAsync(ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return; // nothing readable: nothing to repair (the store reports its own fault)
    }

    HashSet<Guid> owners = [.. inProcessLive(), .. declaredLive()];
    foreach (AgentRecord record in listed.Value)
    {
      if (record.Status is not AgentStatus.Running || owners.Contains(record.Id.Value))
      {
        continue;
      }

      Result<string> marked = await store.UpdateAsync(record with
      {
        Status = AgentStatus.Failed,
        FailureReason = AgentFailureReason.Interrupted,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = "Error [Interrupted]: no live owner for this agent at startup (orphan repair).",
      }, ct).ConfigureAwait(false);
      if (!marked.IsSuccess)
      {
        continue;
      }

      if (audit is not null)
      {
        try
        {
          _ = await audit.AppendAsync(new WatchdogEvent(Guid.NewGuid(), record.Id,
              WatchdogEventKind.TerminalReport, "orphan repair: Running with no live owner -> Failed(Interrupted)",
              record.Attempts, null, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        }
        // Named decision (CA1031): audit is best-effort by contract — a failed
        // event write never blocks the repair it records.
#pragma warning disable CA1031 // Do not catch general exception types
        catch
        {
          // Swallowed deliberately: see the named decision above.
        }
#pragma warning restore CA1031
      }
    }
  }
}
