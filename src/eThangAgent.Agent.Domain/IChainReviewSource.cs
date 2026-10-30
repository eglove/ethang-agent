using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Where the chain records and reads a step's REVIEW (issue #320).
///     <para>WHY A PORT: the review record lives in the Plan Domain (<c>plan_reviews</c>,
///     <c>PlanReview</c>, <c>IPlanReviewStore</c>) and the Agent Domain does not reference it —
///     the boundary <see cref="IChainPlanSource"/> and <see cref="IChainBriefSource"/>
///     already hold for the plan and the gate. The composition root implements this over the
///     real review store, so the cursor can act on a verdict without the Agent Domain learning
///     what a review row is.</para>
///     <para>ONE PORT, TWO READERS (issue #216 D7): the chain cursor deciding whether to
///     advance and the telemetry report (#309) computing calibration read the same rows through
///     the same store, so a reported number and an advance decision cannot disagree.</para>
///     <para>A WRITE IS APPEND-ONLY, exactly as the record is: a verdict is a historical fact,
///     and revising one means recording a new review.</para></summary>
public interface IChainReviewSource
{
  /// <summary>Records <paramref name="review"/> against <paramref name="planId"/> and returns
  ///     <c>true</c> — the caller needs only to know it landed.
  ///     <para>REFUSED WHEN THE REVIEW CANNOT BE WRITTEN, and the refusal is returned rather than
  ///     swallowed: a reviewer told 'your verdict was recorded' when it was not would then mark
  ///     its step done, and the chain would advance on a verdict nobody kept. That is the silent
  ///     discard this issue exists to close, so it must not survive as a best-effort write.</para>
  ///     </summary>
  /// <param name="planId">The plan the review judges.</param>
  /// <param name="planVersion">The exact plan version the reviewer saw.</param>
  /// <param name="review">The verdict, the cited findings, and the reviewer's model.</param>
  /// <param name="reviewerSessionId">The session that produced the verdict.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<bool>> RecordAsync(int planId, int planVersion, ChainReviewRecord review,
      string reviewerSessionId, CancellationToken ct = default);

  /// <summary>The review a session recorded, or null when it recorded none.
  ///     <para>KEYED ON THE SESSION, because that is what the completion fact carries and what the
  ///     record already stores (<c>reviewer_session_id</c>). Per-step counting therefore uses the
  ///     session id rather than a chain-step column on <c>plan_reviews</c>: the column would be a
  ///     second copy of a fact the review row already holds, and #320's own criterion says so.
  ///     </para>
  ///     <para>NULL MEANS 'NO REVIEW WAS RECORDED', never 'an unreadable one': an unreadable row
  ///     is a typed failure, because a cursor that treated a corrupt verdict as an absent one
  ///     would refuse the advance with the wrong reason.</para></summary>
  /// <param name="planId">The plan the review judges.</param>
  /// <param name="reviewerSessionId">The session that produced it.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainReviewRecord?>> FindForSessionAsync(int planId, string reviewerSessionId,
      CancellationToken ct = default);
}
