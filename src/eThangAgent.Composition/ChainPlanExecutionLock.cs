using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Answers 'is a chain executing this plan' from the CHAIN record. The Plan Domain asks
///     through <see cref="IPlanExecutionLock"/>; this adapter reads the chain store, so neither
///     domain has to know the other.
///     <para>A CHAIN THAT IS NOT RUNNING LOCKS NOTHING. Parked waits for a human and may be
///     abandoned or resumed, and Completed or Abandoned are frozen records whose positions
///     nobody will read again - so only Running is a lock.</para>
///     <para>AN UNREADABLE CHAIN LIST ANSWERS 'NOT LOCKED', deliberately: the plan store reports
///     its own faults, and turning a chain-store fault into a lock would make every plan in the
///     workspace uneditable for a reason the caller cannot see or fix.</para></summary>
public sealed class ChainPlanExecutionLock(IChainStore chains) : IPlanExecutionLock
{
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <inheritdoc />
  public async Task<int?> ExecutingChainAsync(int planId, CancellationToken ct = default)
  {
    Result<ChainCatalog> listed = await _chains.ListAsync(ChainStatus.Running, ct).ConfigureAwait(false);
    return !listed.IsSuccess
        ? null
        : listed.Value.Chains
            .Where(chain => chain.PlanId == planId)
            .Select(chain => (int?)chain.Id)
            .FirstOrDefault();
  }
}
