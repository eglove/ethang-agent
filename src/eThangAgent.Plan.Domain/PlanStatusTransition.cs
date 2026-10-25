using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>
/// Only Active plans may leave the Active state, and only into a terminal status:
/// Active -> Completed / Active -> Abandoned. The target is a constructor argument;
/// the Specification evaluates the (from, to) pair against a candidate plan.
/// <para>Completion additionally demands a CONSISTENT plan (issue #271): every step is
/// either Done or explicitly Skipped. Before this, a plan could be completed with all
/// its steps still Pending, so the store recorded a contradiction (#268 measured 22 of
/// 45 completed plans carrying zero done steps; this workspace now shows 33 of 48). The refusal NAMES the offending positions, so the
/// model can act without a plan.show round trip.</para>
/// <para>Abandoned is deliberately unaffected: dropping a half-finished plan is the
/// honest exit, and refusing it would leave no truthful way out. Skipped is what makes
/// the completion gate honest in the other direction - without it the cheapest way past
/// the refusal would be to mark unfinished work Done.</para>
/// </summary>
public sealed class PlanStatusTransition(PlanStatus target) : Specification<Plan>
{
  public override bool IsSatisfiedBy(Plan candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
        && (target != PlanStatus.Completed || UnfinishedSteps(candidate).Count == 0);
  }

  protected override string FailureMessageFor(Plan candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
      ? UnfinishedMessage(candidate)
      : "only 'active' plans can transition to 'completed' or 'abandoned'";
  }

  private bool IsLegalMove(Plan candidate) =>
    candidate.Status == PlanStatus.Active && target is PlanStatus.Completed or PlanStatus.Abandoned;

  /// <summary>Steps that are neither Done nor Skipped, in position order.</summary>
  private static List<PlanStep> UnfinishedSteps(Plan candidate) =>
    [.. candidate.Steps.Where(step => step.Status is not (PlanStepStatus.Done or PlanStepStatus.Skipped))
        .OrderBy(step => step.Position)];

  private static string UnfinishedMessage(Plan candidate)
  {
    List<PlanStep> unfinished = UnfinishedSteps(candidate);
    StringBuilder list = new();
    for (int i = 0; i < unfinished.Count; i++)
    {
      _ = list.Append(i == 0 ? string.Empty : ", ")
          .Append(CultureInfo.InvariantCulture, $"{unfinished[i].Position} ({unfinished[i].Status})");
    }

    return $"plan #{candidate.Id} cannot be completed: {unfinished.Count} step(s) are unfinished: {list}. "
        + "Mark each step Done, or Skipped when you are deliberately not doing it, with update-step first.";
  }
}
