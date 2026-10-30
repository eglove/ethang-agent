using System.Globalization;
using System.Text;

namespace eThangAgent.AgentDomain;

/// <summary>The review round-trip's bound and the decision a recorded verdict produces
///     (issue #320, stakeholder decision Q14).
///     <para>TWO COUNTERS, NOT ONE (D6/Q14). A RETRY means the session broke; a DENY means the
///     work is wrong. Sharing one counter would let a flaky provider consume the review budget
///     and a demanding reviewer consume the retry budget, so each has its own:
///     <see cref="ChainRetryPolicy.MaxAttempts"/> bounds failures, this bounds round trips.
///     </para>
///     <para>WHY A CONSTANT AND NOT A SETTING: the same reason
///     <see cref="ChainRetryPolicy.MaxAttempts"/> and <see cref="ChainClaimPolicy.ClaimTtl"/>
///     give — a configurable cap is one that can be set to never fire, which is the same as
///     having none. Widening it is a product decision that updates this constant and its test
///     in the same change.</para>
///     <para>PURE, like <see cref="ChainAdvancePolicy"/>: facts in, decision out, no I/O and no
///     clock. The enactment (reopen the step, start the fix session, park the chain) lives in
///     the application layer, so the rule is unit-testable with no store and cannot acquire a
///     side effect by accident.</para></summary>
public static class ChainReviewPolicy
{
  /// <summary>How many times a review may send the same step back before the chain parks.
  ///     <para>Counted on the step being REOPENED, so the bound is on round trips for one piece
  ///     of work rather than on one reviewer: a chain that revises a step three times and is
  ///     denied three times is a disagreement a human has to settle, and a fourth round would
  ///     be the loop this cap exists to stop.</para></summary>
  public const int MaxReviewAttempts = 3;

  /// <summary>True when a step sent back <paramref name="reviewAttempts"/> times may be sent
  ///     back again.
  ///     <para>The boundary is the cap itself: at exactly
  ///     <see cref="MaxReviewAttempts"/> the budget is SPENT, because the count is round trips
  ///     already made, not round trips remaining.</para></summary>
  /// <param name="reviewAttempts">Review round trips already recorded on the step.</param>
  public static bool CanSendBack(int reviewAttempts)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(reviewAttempts);
    return reviewAttempts < MaxReviewAttempts;
  }

  /// <summary>What a recorded verdict does to the cursor.
  ///     <para>ORDER IS THE ORDER THE ANSWERS MATTER IN, and each is a different act:</para>
  ///     <list type="bullet">
  ///     <item>a MISSING or UNREADABLE review is a refusal — the reviewer was told to record a
  ///     verdict, so a review step that completed without one did not do its job, and advancing
  ///     past it would be the silent discard this issue exists to close</item>
  ///     <item>a DENIAL with no findings is a refusal for the same shape of reason: a session
  ///     told 'the reviewer denied' with no notes will guess (issue #219's rule)</item>
  ///     <item>a DENIAL with no step to send back is a refusal: there is nothing to reopen, which
  ///     is a chain declaration defect rather than something to retry</item>
  ///     <item>a DENIAL past the cap PARKS, naming the step: the disagreement is waiting for a
  ///     human, not broken</item>
  ///     <item>a DENIAL under the cap sends the step BACK</item>
  ///     <item>otherwise the chain ADVANCES, and an approve-with-conditions carries its findings
  ///     into the next session's brief</item>
  ///     </list></summary>
  /// <param name="review">The recorded verdict, or null when the reviewer recorded none.</param>
  /// <param name="fixTarget">The step a denial would send back, or null when there is none.</param>
  /// <param name="reviewAttempts">Review round trips already recorded on that step.</param>
  public static ChainReviewDecision Decide(ChainReviewRecord? review, int? fixTarget,
      int reviewAttempts)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(reviewAttempts);
    return review is null
        ? ChainReviewDecision.Refused(ChainReviewCode.NoVerdict, NoVerdictMessage)
        : DecideFor(review, fixTarget, reviewAttempts);
  }

  /// <summary>The decision for a review that WAS recorded. Extracted so the missing-review
  ///     case is one statement rather than the first branch of a nested conditional (S3358).
  ///     </summary>
  private static ChainReviewDecision DecideFor(ChainReviewRecord review, int? fixTarget,
      int reviewAttempts)
  {
    return !review.IsDenial
        ? new ChainReviewDecision(ChainReviewDecisionKind.Advance, null,
            ChainReviewNotes.Render(review.Findings), null)
        : DenialFor(review, fixTarget, reviewAttempts);
  }

  /// <summary>The denial half, split out so the two branches read as two statements.</summary>
  private static ChainReviewDecision DenialFor(ChainReviewRecord review, int? fixTarget,
      int reviewAttempts)
  {
    return fixTarget is not { } target
        ? ChainReviewDecision.Refused(ChainReviewCode.NoFixTarget, NoFixTargetMessage)
        : Denial(review, target, reviewAttempts);
  }

  /// <summary>The denial branch: park at the cap, send back under it, refuse an empty
  ///     denial.</summary>
  private static ChainReviewDecision Denial(ChainReviewRecord review, int target,
      int reviewAttempts)
  {
    if (!review.HasFindings)
    {
      return ChainReviewDecision.Refused(ChainReviewCode.NoFindings, NoFindingsMessage);
    }

    string? notes = ChainReviewNotes.Render(review.Findings);
    return CanSendBack(reviewAttempts)
        ? new ChainReviewDecision(ChainReviewDecisionKind.SendBack, target, notes, null)
        : new ChainReviewDecision(ChainReviewDecisionKind.ParkAtCap, target, notes,
            CapReason(target, reviewAttempts));
  }

  /// <summary>The park reason, stated with the counts it is about. Extracted so the cap
  ///     branch reads as one statement and the numbers are formatted invariantly in one place.
  ///     </summary>
  private static string CapReason(int target, int reviewAttempts) =>
      string.Create(CultureInfo.InvariantCulture,
          $"the review of step {target} has sent it back {reviewAttempts} time(s), reaching the {MaxReviewAttempts}-round-trip cap. The reviewer and the implementer disagree, and another round would repeat it - a human has to settle it.");

  private const string NoVerdictMessage =
      "this session's step is a REVIEW step but no review was recorded, so there is no verdict "
      + "to act on. Record it with plan.review (verdict, scores, and findings with citations) "
      + "before marking the step done.";

  private const string NoFixTargetMessage =
      "the reviewer denied, but this chain has no preceding step to send back: a review at the "
      + "chain's first position judges nothing that can be reopened.";

  private const string NoFindingsMessage =
      "the reviewer denied with no findings, so a fix session would be told 'the reviewer denied' "
      + "with nothing to act on. A denial must carry at least one cited finding.";
}

/// <summary>The named refusals the review path produces (issue #320). Each is a distinct
///     answer a reader acts on differently, so collapsing them would tell a reviewer 'no'
///     without saying which part of its job was missing.</summary>
public static class ChainReviewCode
{
  /// <summary>A review step completed with no review recorded anywhere.</summary>
  public const string NoVerdict = "ChainReviewMissing";

  /// <summary>A denial that carries no findings.</summary>
  public const string NoFindings = "ChainReviewNoFindings";

  /// <summary>A denial with no preceding step to reopen.</summary>
  public const string NoFixTarget = "ChainReviewNoFixTarget";

  /// <summary>The round-trip cap is spent: the chain parked naming the step.</summary>
  public const string CapReached = "ChainReviewCapReached";

  /// <summary>The review record could not be read, so nothing was decided.</summary>
  public const string Unreadable = "ChainReviewUnreadable";
}

/// <summary>What a recorded review does next (issue #320).</summary>
public enum ChainReviewDecisionKind
{
  /// <summary>The chain advances; <see cref="ChainReviewDecision.ReviewNotes"/> rides the next
  ///     brief when the reviewer attached conditions.</summary>
  Advance,

  /// <summary>The step at <see cref="ChainReviewDecision"/>'s position is reopened for a fix
  ///     session seeded with the findings.</summary>
  SendBack,

  /// <summary>The round-trip cap is spent: park the chain waiting for a human.</summary>
  ParkAtCap,

  /// <summary>Nothing was started and nothing advanced; the code names why.</summary>
  Refused,
}

/// <summary>The review decision (issue #320): a kind plus the facts the enactment needs.
///     <para><see cref="Target"/> is set only when a step is being reopened, and
///     <see cref="ReviewNotes"/> only when there is something for the next session to address —
///     so a caller cannot mistake a refused position for a fix target.</para></summary>
/// <param name="Kind">What happens next.</param>
/// <param name="Target">The 1-based step position to reopen, or null.</param>
/// <param name="ReviewNotes">The cited findings to render into the next brief, or null.</param>
/// <param name="Reason">Why the chain parked, or null.</param>
/// <param name="Code">The named refusal code, or null when nothing was refused. It is a
///     member rather than part of <paramref name="Reason"/> so a caller branches on the code
///     and renders the sentence, exactly as <see cref="ChainAdvance"/> does.</param>
public sealed record ChainReviewDecision(ChainReviewDecisionKind Kind, int? Target,
    string? ReviewNotes, string? Reason, string? Code = null)
{
  /// <summary>True when the harness has work to do: advance, or reopen and start a fix.
  ///     <para>A refusal and a cap park are NOT actionable here — the refusal leaves the session
  ///     open to read why, and the park has already written its reason to the record.</para>
  ///     </summary>
  public bool IsActionable =>
      Kind is ChainReviewDecisionKind.Advance or ChainReviewDecisionKind.SendBack;

  /// <summary>A refusal carrying its named code and the sentence explaining it.</summary>
  /// <param name="code">The named refusal code.</param>
  /// <param name="reason">The sentence explaining it.</param>
  public static ChainReviewDecision Refused(string code, string reason)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return new ChainReviewDecision(ChainReviewDecisionKind.Refused, null, null, reason, code);
  }

  public override string ToString()
  {
    if (Kind == ChainReviewDecisionKind.Advance)
    {
      return ReviewNotes is null ? "advance" : "advance with conditions";
    }

    string target = Target is { } position
        ? string.Create(CultureInfo.InvariantCulture, $" step {position}")
        : string.Empty;
    return Reason is { } reason ? $"{Kind}{target}: {reason}" : $"{Kind}{target}";
  }
}

/// <summary>Renders a review's cited findings as the notes a session must address (issue
///     #320).
///     <para>THE FINDINGS ARE CARRIED VERBATIM WITH THEIR CITATIONS, never summarised.
///     Issue #219's surviving rule: the citation is what makes a note actionable, and a
///     summary is a second lossy step between the reviewer and the planner — the planner would
///     be acting on somebody's paraphrase of the reviewer rather than on the reviewer.</para>
///     <para>PURE: the same findings always render the same text, so a resumed fix session
///     reads exactly what the first launch read.</para></summary>
public static class ChainReviewNotes
{
  /// <summary>The rendered notes, or null when there is nothing to say.
  ///     <para>NULL, NOT AN EMPTY STRING, for an empty list: 'no notes' and 'notes that render
  ///     to nothing' are the same fact, and a caller checking for null would otherwise have to
  ///     check both.</para></summary>
  /// <param name="findings">The reviewer's findings.</param>
  public static string? Render(IReadOnlyList<ChainReviewFinding> findings)
  {
    ArgumentNullException.ThrowIfNull(findings);
    List<ChainReviewFinding> usable = [.. findings.Where(finding => finding.IsWellFormed)];
    if (usable.Count == 0)
    {
      return null;
    }

    StringBuilder sb = new();
    // INDEXED, not compared against the last element: records compare by VALUE, so two
    // findings that say the same thing would be equal and the separator between them lost.
    for (int i = 0; i < usable.Count; i++)
    {
      if (i > 0)
      {
        _ = sb.Append('\n');
      }

      _ = sb.Append("- ").Append(usable[i].Text.Trim())
          .Append(" [cites: ").Append(usable[i].Citation.Trim()).Append(']');
    }

    return sb.ToString();
  }
}
