using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Records an implementation review and answers what the cursor reads.
///     <para>THE RECORD IS THE REVIEW ROW, not the step's free-text outcome. A verdict is a typed
///     fact with cited findings, and a text field is where it goes to be re-parsed badly. The
///     cursor reads the row; a reader of the chain record needs no join to see what happened.
///     </para>
///     <para>IT REFUSES RATHER THAN GUESSES. A write that does not land is reported, because a
///     reviewer told 'recorded' when it was not would mark its step done and the chain would
///     advance on a verdict nobody kept.</para></summary>
/// <param name="reviews">The store the composition root implements.</param>
/// <param name="chains">The chain record, for the chain a review belongs to.</param>
public sealed class ChainReviewService(IChainReviewSource reviews, ChainService chains)
{
  private readonly IChainReviewSource _reviews = reviews
      ?? throw new ArgumentNullException(nameof(reviews));
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <summary>Records <paramref name="review"/> for the chain step <paramref name="sessionId"/>
  ///     holds, and returns the chain the review is part of.
  ///     <para>THE STEP JUDGED IS READ FROM THE RECORD, not from the caller: a review judges the
  ///     work immediately before it, which is what <see cref="Chain.FixTargetFor"/> answers, and
  ///     a caller restating that could record a verdict about work it did not read.</para>
  ///     <para>REFUSED WHEN THE SESSION HOLDS NO CHAIN STEP: a review outside a chain moves no
  ///     chain, and reporting that plainly is better than writing a row whose reader does not
  ///     exist.</para></summary>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="review">The verdict and its cited findings.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<Chain>> RecordAsync(string sessionId, ChainReviewRecord review,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    ArgumentNullException.ThrowIfNull(review);
    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Result.Failure<Chain>(found.Error);
    }

    if (found.Value is not { } chain)
    {
      return Result.Failure<Chain>(new DomainError(ChainReviewCode.NoVerdict,
          $"session {sessionId} is not assigned to any chain step, so its review moves no chain."));
    }

    ChainStep? reviewed = chain.Steps.FirstOrDefault(step =>
        string.Equals(step.SessionId, sessionId, StringComparison.Ordinal));
    if (reviewed is null || !reviewed.Role.IsReview)
    {
      return Result.Failure<Chain>(new DomainError(ChainReviewCode.NotAReviewStep,
          $"session {sessionId} holds no review step of chain #{chain.Id}; only a step assigned the "
          + "review role records a verdict on implementation."));
    }

    // THE WORK THE REVIEW JUDGES: the nearest completed step before it.
    int judged = chain.FixTargetFor(reviewed.Position)?.Position ?? reviewed.Position;
    Result<ChainReviewRecord> recorded = await _reviews
        .RecordAsync(chain.Id, judged, review, sessionId, now, ct).ConfigureAwait(false);
    return recorded.IsSuccess ? Result.Success(chain) : Result.Failure<Chain>(recorded.Error);
  }

  /// <summary>The review <paramref name="sessionId"/> recorded for a chain, or null when it
  ///     recorded none.
  ///     <para>THE CURSOR'S ONE READ. It answers 'what did this reviewer say', which is the
  ///     question <see cref="ChainReviewPolicy.Decide"/> needs and the only one the advance asks.
  ///     </para></summary>
  /// <param name="chainId">The chain the review belongs to.</param>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="ct">Cancellation.</param>
  public Task<Result<ChainReviewRecord?>> FindAsync(int chainId, string sessionId,
      CancellationToken ct = default) => _reviews.FindForSessionAsync(chainId, sessionId, ct);
}
