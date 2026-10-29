using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Answers 'may this session complete this plan step' from the CHAIN record (issue
///     #301 D3). The Plan Domain asks through <see cref="IPlanStepOwnershipGate"/>; this
///     adapter reads the chain store, so neither domain has to know the other.
///     <para>THE RULE, in one sentence: a session executing a chain step may complete only the
///     step it was started for, and a session executing NO chain step is unaffected.</para>
///     <para>WHY THE SECOND HALF MATTERS AS MUCH AS THE FIRST: the ordinary way a plan is
///     worked is a person (or a hand-started session) marking steps Done. If the gate refused
///     every session without a chain assignment, it would break the only workflow plans have
///     ever had, to protect a workflow that is not running.</para>
///     <para>THE PLAN MUST ALSO MATCH. A session assigned to chain #4's step 2 may not complete
///     step 2 of an UNRELATED plan - the position alone is not the assignment.</para>
///     <para>THE CLAIM MUST BE LIVE. An expired claim is no longer current, so a completion
///     under it is refused with its own code rather than silently accepted (the issue's
///     amendment); re-claiming records the new claimant.</para></summary>
public sealed class ChainStepOwnershipGate(IChainStore chains, TimeProvider clock)
    : IPlanStepOwnershipGate
{
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  /// <inheritdoc />
  public async Task<PlanStepOwnership> EvaluateAsync(int planId, int position, string sessionId,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      // The chain record could not be read. Refusing is the safe direction: letting the write
      // through on an unreadable record would advance a chain nobody can see.
      return PlanStepOwnership.Refuse(found.Error.Code, found.Error.Message);
    }

    // Not executing a chain step: the ordinary case, and it passes.
    if (found.Value is not { } chain)
    {
      return PlanStepOwnership.Permit;
    }

    ChainStep? assigned = chain.Steps.FirstOrDefault(step =>
        string.Equals(step.SessionId, sessionId, StringComparison.Ordinal));
    return assigned is null
      ? PlanStepOwnership.Permit
      : WrongStep(chain, assigned, planId, position) ?? LapsedClaim(chain, assigned) ?? PlanStepOwnership.Permit;
  }

  /// <summary>The assignment must be THIS plan's step at THIS position: the position alone is
  ///     not the assignment, or a session on chain #4's step 2 could complete step 2 of an
  ///     unrelated plan.</summary>
  private static PlanStepOwnership? WrongStep(Chain chain, ChainStep assigned, int planId, int position)
    => assigned.Position == position && chain.PlanId == planId
      ? null
      : PlanStepOwnership.Refuse(ChainAdvanceCode.ForeignStep,
          $"chain #{chain.Id} assigned you step {assigned.Position} of plan #{chain.PlanId}, "
          + $"not step {position} of plan #{planId}; a chain step's session completes only its own step.");

  /// <summary>An expired claim is no longer current, so a completion under it is refused with
  ///     its own code rather than silently accepted (the issue's amendment). Re-claiming
  ///     records the new claimant.</summary>
  private PlanStepOwnership? LapsedClaim(Chain chain, ChainStep assigned)
    => assigned.IsClaimLive(_clock.GetUtcNow())
      ? null
      : PlanStepOwnership.Refuse(ChainAdvanceCode.ClaimLapsed,
          $"chain #{chain.Id} step {assigned.Position}'s claim lapsed at "
          + (assigned.ClaimedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
              ?? "an unrecorded time")
          + $" (claim window {ChainClaimPolicy.ClaimTtl}); re-claim the step before completing it.");
}
