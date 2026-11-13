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
///     step 2 of an UNRELATED plan - the position alone is not the assignment.</para></summary>
public sealed class ChainStepOwnershipGate(IChainStore chains) : IPlanStepOwnershipGate
{
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));

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
    // NOT EXECUTING A CHAIN STEP: the ordinary case, and it passes with the workspace gate
    // applying as it always has.
    return assigned is null ? PlanStepOwnership.Permit : Assigned(chain, assigned, planId, position);
  }

  /// <summary>The answer for a session that IS executing a chain step: the wrong-step refusal,
  ///     or a permit whose evidence is the plan when the step is a refine step.
  ///     <para>A REFINE STEP'S EVIDENCE IS THE PLAN. It changes the plan rather than the
  ///     workspace, so no command can show that a step became checkable, and the workspace gate
  ///     would refuse the one claim this role can make. The flag is only ever set for a step the
  ///     session actually holds, which is what keeps it from widening any other completion.
  ///     </para></summary>
  /// <param name="chain">The chain the session holds.</param>
  /// <param name="assigned">The step it holds.</param>
  /// <param name="planId">The plan being updated.</param>
  /// <param name="position">The 1-based step position being completed.</param>
  private static PlanStepOwnership Assigned(Chain chain, ChainStep assigned, int planId,
      int position) =>
      WrongStep(chain, assigned, planId, position) ?? PlanStepOwnership.Permit with
      {
        BackedByThePlan = assigned.Role.IsRefine,
      };

  /// <summary>The assignment must be THIS plan's step at THIS position: the position alone is
  ///     not the assignment, or a session on chain #4's step 2 could complete step 2 of an
  ///     unrelated plan.</summary>
  private static PlanStepOwnership? WrongStep(Chain chain, ChainStep assigned, int planId, int position)
    => assigned.Position == position && chain.PlanId == planId
      ? null
      : PlanStepOwnership.Refuse(ChainAdvanceCode.ForeignStep,
          $"chain #{chain.Id} assigned you step {assigned.Position} of plan #{chain.PlanId}, "
          + $"not step {position} of plan #{planId}; a chain step's session completes only its own step.");

}
