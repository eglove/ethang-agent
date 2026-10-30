using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>What <c>plan.review</c> collects before anything is written (issue #320): the
///     verdict, the three rubric scores, and the cited findings, all as the model stated them.
///     <para>WHY A DRAFT TYPE AND NOT A <see cref="PlanReview"/>: the reviewer does not know
///     the session id or the plan version it is judging - the harness resolves both, because a
///     reviewer that named its own version could record a verdict against content nobody
///     reviewed. The draft is exactly what the CALLER can honestly supply.</para>
///     <para>IT CARRIES NO <c>Id</c> AND NO TIMESTAMP for the same reason: a persisted row's
///     identity and clock belong to the store, not to the model.</para></summary>
/// <param name="PlanId">The plan the review judges.</param>
/// <param name="Verdict">What the reviewer concluded.</param>
/// <param name="Scores">The three rubric dimensions.</param>
/// <param name="Findings">The cited findings; empty is legal for a clean approve.</param>
/// <param name="Notes">Optional free-text context from the reviewer.</param>
public sealed record PlanReviewDraft(int PlanId, PlanReviewVerdict Verdict,
    PlanReviewScores Scores, IReadOnlyList<PlanReviewFinding> Findings, string? Notes = null)
{
  /// <summary>The write-time gate, applied before the draft leaves the provider so a malformed
  ///     verdict is refused with the same named codes a stored record would earn.
  ///     <para>THE STORE CHECKS AGAIN, deliberately: this refuses early so the model gets a
  ///     readable error, and the store refuses late so a caller that bypassed the provider
  ///     cannot write a bad row. Two boundaries, one rule.</para></summary>
  /// <param name="planVersion">The version the harness resolved for this review.</param>
  /// <param name="reviewerSessionId">The session the harness attributed the review to.</param>
  /// <param name="reviewerModel">The model that session served on, when known.</param>
  public Result<PlanReview> ToReview(int planVersion, string reviewerSessionId,
      string? reviewerModel) =>
      new PlanReview(0, PlanId, planVersion, Verdict, reviewerSessionId, reviewerModel, Scores,
          Findings, Notes, DateTimeOffset.UtcNow).Validate();
}
