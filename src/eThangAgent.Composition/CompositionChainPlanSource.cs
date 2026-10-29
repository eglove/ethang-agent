using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Implements the Agent Domain's plan port over the real plan store (issue #313).
///     <para>WHY HERE AND NOT IN THE AGENT DOMAIN: the plan record lives in the Plan Domain,
///     which the Agent Domain does not reference. The composition root is the one place
///     allowed to know both, and it is the same shape <c>CompositionChainBriefSource</c>
///     already takes for the brief's facts.</para>
///     <para>THE WORKSPACE COMES FROM THE PLAN, not from this container's context. A chain
///     must be scoped identically to the plan it executes, and the plan store is itself
///     workspace-bound — so a plan this container can read IS in this workspace. Reading the
///     id from the plan keeps the two records from drifting if a caller ever creates a chain
///     for a plan it read through another route.</para></summary>
internal sealed class CompositionChainPlanSource(PlanService plans, IWorkspaceContext workspace)
    : IChainPlanSource
{
  private readonly PlanService _plans = plans ?? throw new ArgumentNullException(nameof(plans));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  /// <inheritdoc />
  public async Task<Result<ChainPlanFacts>> GetPlanAsync(int planId, CancellationToken ct = default)
  {
    Result<Plan> plan = await _plans.GetAsync(planId, ct).ConfigureAwait(false);
    return plan.IsSuccess
        ? Result.Success(new ChainPlanFacts(plan.Value.Id, plan.Value.Version, plan.Value.Title,
            _workspace.WorkspaceId, [.. plan.Value.Steps.Select(step => step.Position)]))
        : Result.Failure<ChainPlanFacts>(plan.Error);
  }
}
