using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Records a reviewer's verdict (issue #320): the writer nothing in this harness had.
///     <para>THE DEFECT THIS CLOSES: <c>ChainContractRenderer.AddReview</c> told every reviewer
///     to "record your verdict and your cited findings in this step's outcome", and the advance
///     settled the step with <c>outcome: null</c> — so a reviewer did the work and nothing kept
///     it. The verdict reached no record at all, which meant the cursor had nothing to read and
///     the deny path could not exist.</para>
///     <para>THE RECORD IS THE REVIEW ROW, not the step's free-text outcome. A verdict is a
///     typed fact with scores and citations (issue #216), and a text field is where it goes to
///     be re-parsed badly. The step outcome is written too — a reader of the chain record should
///     not have to join to see what happened — but the row is the source the cursor reads.
///     </para>
///     <para>IT REFUSES RATHER THAN GUESSES. A write that does not land is reported, because a
///     reviewer told 'recorded' when it was not would mark its step done and the chain would
///     advance on a verdict nobody kept — which is exactly the silent discard this closes.
///     </para></summary>
/// <param name="reviews">The record port the composition root implements over the plan review
///     store.</param>
/// <param name="chains">The chain record, for the plan and version the review judges.</param>
public sealed class ChainReviewService(IChainReviewSource reviews, ChainService chains)
{
  private readonly IChainReviewSource _reviews = reviews
      ?? throw new ArgumentNullException(nameof(reviews));
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <summary>Records <paramref name="review"/> for the chain step
  ///     <paramref name="sessionId"/> holds, and returns the chain the review judges.
  ///     <para>THE PLAN AND VERSION COME FROM THE CHAIN, not from the caller: the chain records
  ///     the plan version it STARTED at (D8), and a review must judge that version rather than
  ///     whatever the plan has been revised to since. A caller restating them could record a
  ///     verdict against content nobody reviewed.</para>
  ///     <para>REFUSED WHEN THE SESSION HOLDS NO CHAIN STEP: a review outside a chain is the
  ///     ordinary <c>plan.review</c> case with no chain to move, and reporting that plainly is
  ///     better than writing a row whose reader does not exist.</para></summary>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="review">The verdict, the cited findings, and the reviewer's model.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<Chain>> RecordAsync(string sessionId, ChainReviewRecord review,
      CancellationToken ct = default)
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

    Result<bool> recorded = await _reviews
        .RecordAsync(chain.PlanId, chain.PlanVersion, review, sessionId, ct).ConfigureAwait(false);
    return recorded.IsSuccess ? Result.Success(chain) : Result.Failure<Chain>(recorded.Error);
  }

  /// <summary>The review <paramref name="sessionId"/> recorded for <paramref name="planId"/>,
  ///     or null when it recorded none.
  ///     <para>THE CURSOR'S ONE READ. It answers 'what did this reviewer say', which is the
  ///     question <see cref="ChainReviewPolicy.Decide"/> needs and the only one the advance
  ///     asks.</para></summary>
  /// <param name="planId">The plan the chain executes.</param>
  /// <param name="sessionId">The reviewing session.</param>
  /// <param name="ct">Cancellation.</param>
  public Task<Result<ChainReviewRecord?>> FindAsync(int planId, string sessionId,
      CancellationToken ct = default) => _reviews.FindForSessionAsync(planId, sessionId, ct);
}
