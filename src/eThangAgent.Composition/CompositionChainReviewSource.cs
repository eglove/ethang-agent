using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Implements the Agent Domain's review port over the real plan review store (issue
///     #320): THE WRITER nothing in this harness had.
///     <para>WHY HERE AND NOT IN THE AGENT DOMAIN: the review record lives in the Plan Domain
///     (<c>PlanReview</c>, <c>IPlanReviewStore</c>, <c>PlanReviewVerdict</c>) and the Agent
///     Domain does not reference it - the boundary <c>CompositionChainBriefSource</c> and
///     <c>CompositionChainPlanSource</c> already hold for the plan and the gate. The composition
///     root is the one place allowed to know both, so the translation between the two verdict
///     spellings lives here and nowhere else.</para>
///     <para>THE TRANSLATION IS EXHAUSTIVE BY CONSTRUCTION. <see cref="Map"/> switches over
///     every <see cref="ChainReviewVerdict"/> member, so adding one without mapping it is a
///     compile error rather than a silent default - and a test pins the two token sets against
///     each other so the spellings cannot drift.</para>
///     <para>THE SCORES ARE NOT ASKED FOR HERE. <c>plan.review</c> carries them from the
///     reviewer (the rubric's three dimensions, #217); this port records the verdict and the
///     findings the CURSOR needs, and re-scores nothing. A port that invented scores would put
///     numbers in the record that no reviewer wrote.</para></summary>
internal sealed class CompositionChainReviewSource(IPlanReviewStore reviews) : IChainReviewSource
{
  private readonly IPlanReviewStore _reviews = reviews
      ?? throw new ArgumentNullException(nameof(reviews));

  /// <inheritdoc />
  public async Task<Result<bool>> RecordAsync(int planId, int planVersion,
      ChainReviewRecord review, string reviewerSessionId, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(review);
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
    ChainReviewScores scores = review.EffectiveScores;
    PlanReview draft = new(0, planId, planVersion, Map(review.Verdict),
        reviewerSessionId, review.ReviewerModel,
        new PlanReviewScores(scores.RequirementCoverage, scores.Testability, scores.BlastRadius),
        [.. review.Findings.Select(finding => new PlanReviewFinding(finding.Text, finding.Citation))],
        Notes: null,
        DateTimeOffset.UtcNow);
    Result<PlanReview> created = await _reviews.CreateAsync(draft, ct).ConfigureAwait(false);
    return created.IsSuccess ? Result.Success(true) : Result.Failure<bool>(created.Error);
  }

  /// <inheritdoc />
  public async Task<Result<ChainReviewRecord?>> FindForSessionAsync(int planId,
      string reviewerSessionId, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
    Result<IReadOnlyList<PlanReview>> listed = await _reviews
        .ListForPlanAsync(planId, ct).ConfigureAwait(false);
    if (!listed.IsSuccess)
    {
      return Result.Failure<ChainReviewRecord?>(listed.Error);
    }

    // THE NEWEST REVIEW BY THIS SESSION. A session reviews one step, so the newest is the one its
    // completion fact is about; taking the newest rather than the first keeps a resumed session
    // that somehow reviewed twice acting on what it said LAST.
    PlanReview? found = listed.Value
        .LastOrDefault(entry => string.Equals(entry.ReviewerSessionId, reviewerSessionId,
            StringComparison.Ordinal));
    return Result.Success(found is null ? null : ToChainReview(found));
  }

  /// <summary>The recorded review as the chain reads it.</summary>
  /// <param name="review">The persisted review.</param>
  private static ChainReviewRecord ToChainReview(PlanReview review) =>
      new(Unmap(review.Verdict),
          [.. review.Findings.Select(finding =>
              new ChainReviewFinding(finding.Text, finding.Citation))],
          new ChainReviewScores(review.Scores.RequirementCoverage, review.Scores.Testability,
              review.Scores.BlastRadius),
          review.ReviewerModel);

  /// <summary>The chain verdict as the record's verdict. EXHAUSTIVE, so a new chain verdict
  ///     cannot be added without deciding what it records.</summary>
  /// <param name="verdict">The chain verdict.</param>
  private static PlanReviewVerdict Map(ChainReviewVerdict verdict) => verdict switch
  {
    ChainReviewVerdict.Approve => PlanReviewVerdict.Approve,
    ChainReviewVerdict.Deny => PlanReviewVerdict.Deny,
    ChainReviewVerdict.ApproveWithConditions => PlanReviewVerdict.ApproveWithConditions,
    _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict,
        "not a chain review verdict."),
  };

  /// <summary>The record's verdict as the chain reads it. A value outside the three defined
  ///     members cannot reach here (the store's CHECK constraint and the codec both refuse it),
  ///     so the throw states a defect rather than a reachable state.</summary>
  /// <param name="verdict">The persisted verdict.</param>
  private static ChainReviewVerdict Unmap(PlanReviewVerdict verdict) => verdict switch
  {
    PlanReviewVerdict.Approve => ChainReviewVerdict.Approve,
    PlanReviewVerdict.Deny => ChainReviewVerdict.Deny,
    PlanReviewVerdict.ApproveWithConditions => ChainReviewVerdict.ApproveWithConditions,
    _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict,
        "not a plan review verdict."),
  };
}
