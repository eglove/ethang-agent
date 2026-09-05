using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>
/// Only Active plans may leave the Active state, and only into a terminal status:
/// Active -> Completed / Active -> Abandoned. The target is a constructor argument;
/// the Specification evaluates the (from, to) pair against a candidate plan.
/// </summary>
public sealed class PlanStatusTransition(PlanStatus target) : Specification<Plan>
{
  public override bool IsSatisfiedBy(Plan candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return candidate.Status == PlanStatus.Active && target is PlanStatus.Completed or PlanStatus.Abandoned;
  }

  protected override string FailureMessageFor(Plan candidate) =>
    "only 'active' plans can transition to 'completed' or 'abandoned'";
}
