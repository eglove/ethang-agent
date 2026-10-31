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
/// <para>COMPLETION ALSO DEMANDS NO INVALIDATED STEP (issue #304 D5). A refutation can
/// disprove a conclusion an EARLIER step reached, and the marker records that without
/// changing the step's status (its work genuinely was done). Letting the chain complete on a
/// conclusion its own ledger disproved is the one thing D5 refuses: the chain would carry a
/// finished record whose findings do not hold, and every later reader would act on it. The
/// refusal NAMES the invalidated step and the refutation that invalidated it, so a reader
/// does not have to search the ledger for why.</para>
/// <para>Abandoned is deliberately ungated: dropping a half-finished chain is the honest
/// exit, and refusing it would leave no truthful way out. Clearing an invalidation is the
/// HUMAN's decision (<see cref="Chain.ClearInvalidation"/>) — an agent cannot un-invalidate
/// its own disproof.</para>
/// </summary>
public sealed class ChainStatusTransition(ChainStatus target) : Specification<Chain>
{
  public override bool IsSatisfiedBy(Chain candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
        && (target != ChainStatus.Completed || (UnsettledSteps(candidate).Count == 0
            && candidate.InvalidatedSteps.Count == 0));
  }

  protected override string FailureMessageFor(Chain candidate)
  {
    ArgumentNullException.ThrowIfNull(candidate);
    return IsLegalMove(candidate)
      ? CompletionRefusal(candidate)
      : $"chain #{candidate.Id} is {candidate.Status}; only a running or parked chain can move, and only to a different status.";
  }

  /// <summary>Which completion invariant failed, stated as that one fact. Extracted so the two
  ///     refusals stay separate statements rather than a nested conditional (S3358), and so a
  ///     chain that is BOTH unsettled and invalidated is told the actionable one first.
  ///     </summary>
  private static string CompletionRefusal(Chain candidate)
  {
    List<ChainStep> unsettled = UnsettledSteps(candidate);
    return unsettled.Count > 0
      ? UnsettledMessage(candidate)
      : InvalidatedMessage(candidate);
  }

  /// <summary>The invalidation refusal (issue #304 D5): names each invalidated step and the
  ///     refutation that did it, then names both exits - the human's clear, or abandoning the
  ///     chain. A refusal that offered no exit would make the cheapest way past it a lie.
  ///     </summary>
  private static string InvalidatedMessage(Chain candidate)
  {
    List<ChainStep> invalidated = [.. candidate.InvalidatedSteps];
    System.Text.StringBuilder list = new();
    for (int i = 0; i < invalidated.Count; i++)
    {
      _ = list.Append(i == 0 ? string.Empty : ", ")
          .Append(System.Globalization.CultureInfo.InvariantCulture,
              $"step {invalidated[i].Position} (invalidated by {invalidated[i].InvalidatedBy})");
    }

    return $"chain #{candidate.Id} cannot be completed: {invalidated.Count} step(s) were invalidated "
        + $"by a later refutation: {list}. The work was done, but a conclusion it reached no "
        + "longer holds. A HUMAN decides whether the conclusion still stands - an agent cannot "
        + "un-invalidate its own disproof - and clearing the markers is what lets the chain "
        + "complete. To drop the work instead, set the chain Abandoned.";
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
