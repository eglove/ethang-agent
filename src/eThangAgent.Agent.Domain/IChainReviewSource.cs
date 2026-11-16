using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Where the chain records and reads an implementation REVIEW.
///     <para>ITS OWN STORE, NOT THE PLAN'S. A plan review judges a plan version before anything
///     is built; this judges work that was built. Sharing <c>plan_reviews</c> between them is
///     what let 'review the plan' and 'review the implementation' be argued as one thing, and
///     the cost was a role deleted for a reason that never applied to it.</para>
///     <para>WHY A PORT: the Agent Domain does not reference the Storage ACL. The composition
///     root implements this over the <c>chain_reviews</c> table, so the cursor can act on a
///     verdict without the domain learning what a row is.</para>
///     <para>A WRITE IS APPEND-ONLY: a verdict is a historical fact, and revising one means
///     recording a new review.</para></summary>
public interface IChainReviewSource
{
  /// <summary>Records <paramref name="review"/> against the chain step it judges and returns the
  ///     verdict as stored.
  ///     <para>REFUSED WHEN THE REVIEW CANNOT BE WRITTEN, and the refusal is returned rather than
  ///     swallowed: a reviewer told 'your verdict was recorded' when it was not would then mark
  ///     its step done, and the chain would advance on a verdict nobody kept.</para></summary>
  /// <param name="chainId">The chain the review is part of.</param>
  /// <param name="judgedPosition">The 1-based position of the step being judged.</param>
  /// <param name="review">The verdict and its cited findings.</param>
  /// <param name="reviewerSessionId">The session that produced the verdict.</param>
  /// <param name="now">The clock the record is stamped with.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainReviewRecord>> RecordAsync(int chainId, int judgedPosition,
      ChainReviewRecord review, string reviewerSessionId, DateTimeOffset now,
      CancellationToken ct = default);

  /// <summary>The review <paramref name="reviewerSessionId"/> recorded for the chain, or null
  ///     when it recorded none.
  ///     <para>NULL MEANS 'NO REVIEW WAS RECORDED', never 'an unreadable one': an unreadable row is
  ///     a typed failure, because a cursor that treated a corrupt verdict as an absent one would
  ///     refuse the advance with the wrong reason.</para></summary>
  /// <param name="chainId">The chain the review belongs to.</param>
  /// <param name="reviewerSessionId">The session that produced it.</param>
  /// <param name="ct">Cancellation.</param>
  Task<Result<ChainReviewRecord?>> FindForSessionAsync(int chainId, string reviewerSessionId,
      CancellationToken ct = default);
}
