using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>
/// The chain's legal status moves (issue #300 D4). Running may park, complete, or be
/// abandoned; Parked may resume to Running, complete, or be abandoned. Completed and
/// Abandoned are terminal and frozen.
/// <para>Completion additionally demands a CONSISTENT chain: no step may still be Pending,
/// Running, Failed or Parked. That mirrors the plan gate's shape (#271) for the same
/// reason — a chain that claims it is finished while a step is unfinished is a record that
/// contradicts itself, and a later session reading it has no way to tell which half is
/// true. <see cref="ChainStepStatus.Skipped"/> is the truthful exit: without it the
/// cheapest way past the refusal would be to mark an unfinished step Done.</para>
/// <para>Abandoned is deliberately ungated: dropping a half-finished chain is the honest
/// exit, and refusing it would leave no truthful way out.</para>
/// </summary>
public sealed class ChainStatusTransition(ChainStatus target) : Specification<Chain>
{
  public override bool IsSatisfiedBy(Chain candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
        && (target != ChainStatus.Completed || UnsettledSteps(candidate).Count == 0);
  }

  protected override string FailureMessageFor(Chain candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
      ? UnsettledMessage(candidate)
      : $"chain #{candidate.Id} is {candidate.Status}; only a running or parked chain can move, and only to a different status.";
  }

  private bool IsLegalMove(Chain candidate)
    => candidate.Status is ChainStatus.Running or ChainStatus.Parked && candidate.Status != target;

  /// <summary>Steps that are neither Done nor Skipped, in position order.</summary>
  private static List<ChainStep> UnsettledSteps(Chain candidate)
    => [.. candidate.Steps
        .Where(step => step.Status is not (ChainStepStatus.Done or ChainStepStatus.Skipped))
        .OrderBy(step => step.Position)];

  private static string UnsettledMessage(Chain candidate)
  {
    List<ChainStep> unsettled = UnsettledSteps(candidate);
    System.Text.StringBuilder list = new();
    for (int i = 0; i < unsettled.Count; i++)
    {
      _ = list.Append(i == 0 ? string.Empty : ", ")
          .Append(System.Globalization.CultureInfo.InvariantCulture,
              $"{unsettled[i].Position} ({unsettled[i].Status})");
    }

    return $"chain #{candidate.Id} cannot be completed: {unsettled.Count} step(s) are unsettled: {list}. "
        + "Settle each step Done, or Skipped when you are deliberately not doing it.";
  }
}
