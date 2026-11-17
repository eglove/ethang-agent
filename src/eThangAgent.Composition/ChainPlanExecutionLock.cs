using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Answers 'is a chain executing this plan, and how far does it reach' from the CHAIN
///     record. The Plan Domain asks through <see cref="IPlanExecutionLock"/>; this adapter reads
///     the chain store, so neither domain references the other.
///     <para>ONLY A RUNNING CHAIN IS A LOCK. A parked chain waits for a human who may abandon or
///     resume it, and Completed or Abandoned chains are frozen records.</para>
///     <para>AN UNREADABLE CHAIN LIST ANSWERS 'NOT LOCKED', so a chain-store fault is reported
///     by the store rather than making every plan in the workspace uneditable.</para>
///     <para>THE REACH IS THE HIGHEST POSITION THE CHAIN'S STEPS NAME, not their count: a chain
///     holds the positions it names, which need not be contiguous.</para></summary>
public sealed class ChainPlanExecutionLock(IChainStore chains) : IPlanExecutionLock
{
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <inheritdoc />
  public async Task<PlanExecutionLock?> ExecutionAsync(int planId, CancellationToken ct = default)
  {
    Result<ChainCatalog> listed = await _chains.ListAsync(ChainStatus.Running, ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return null;
    }

    Chain? executing = listed.Value.Chains.FirstOrDefault(chain => chain.PlanId == planId);
    return executing is null
        ? null
        : new PlanExecutionLock(executing.Id, executing.Steps.Max(step => step.Position));
  }
}
