using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>What enacting a recorded verdict did, stated so the reviewer's caller can report it.
///     <para>THE VERDICT IS THE COMPLETION. A review step's work is judging, and the record of
///     that work is the verdict row - so recording it settles the step and moves the cursor.
///     A reviewer that also marked its own step done would be completing work the harness had
///     already completed, and a reviewer told not to (because a denial is not a note) would
///     freeze the chain forever. Both shapes are the same defect: the step's completion was a
///     second act the verdict did not perform.</para></summary>
/// <param name="ChainId">The chain the verdict moved.</param>
/// <param name="ReviewPosition">The review step that was settled.</param>
/// <param name="Kind">What the verdict decided.</param>
/// <param name="Target">The step reopened, or null.</param>
/// <param name="SessionId">The session started for the next or reopened step, or null.</param>
/// <param name="Reason">Why the chain parked, or null.</param>
public sealed record ChainVerdictOutcome(int ChainId, int ReviewPosition,
    ChainReviewDecisionKind Kind, int? Target, string? SessionId, string? Reason)
{
  /// <summary>True when this enactment moved the chain: it settled the review, started a
  ///     session, or parked it. False when there was nothing to enact.</summary>
  public bool Moved => Kind != ChainReviewDecisionKind.Refused;
}

/// <summary>Enacts a recorded review verdict (issue #320's missing half).
///     <para>THE DEADLOCK THIS CLOSES. The cursor reads a verdict only when the review step
///     settles, and the skill tells a reviewer not to settle its own step after a denial -
///     because a denial is not a note and marking it done would advance the chain with the
///     findings unfixed. Those two rules together are a chain that can never move: the verdict
///     is recorded, the step stays Running, and nothing reads it. A batch run hit exactly that
///     and left a chain Running with its work finished.</para>
///     <para>SO THE VERDICT SETTLES ITS OWN STEP. Recording is the act; the settle and the
///     decision follow from it in one call, and a reviewer that also marks its step done
///     afterwards is answered with 'already settled' rather than advancing twice.</para>
///     <para>THE RULE IS <see cref="ChainReviewPolicy"/>; this class only performs what the
///     rule decided - the <see cref="ChainAdvanceService"/> split, applied to the verdict.</para>
///     <para>IT USES THE RESUME LAUNCH PORT, not the advance's host: a verdict can be recorded
///     from the child host, which can start a session through the wire but has no shell. The
///     port is the one seam both containers implement.</para></summary>
/// <param name="chains">The chain application service.</param>
/// <param name="reviews">Reads the verdict the reviewer recorded.</param>
/// <param name="launch">Starts the session the decision names.</param>
public sealed class ChainVerdictService(ChainService chains, ChainReviewService reviews,
    IChainResumeLaunchPort launch)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly ChainReviewService _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
  private readonly IChainResumeLaunchPort _launch = launch ?? throw new ArgumentNullException(nameof(launch));

  /// <summary>Settles the review step <paramref name="reviewerSessionId"/> holds and enacts the
  ///     verdict it recorded. Idempotent: a step that already settled enacts nothing.</summary>
  /// <param name="chainId">The chain the reviewer belongs to.</param>
  /// <param name="reviewerSessionId">The reviewing session.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainVerdictOutcome>> EnactAsync(int chainId, string reviewerSessionId,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
    Result<Chain> loaded = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<ChainVerdictOutcome>(loaded.Error);
    }

    Chain chain = loaded.Value;
    ChainStep? review = chain.Steps.FirstOrDefault(step =>
        string.Equals(step.SessionId, reviewerSessionId, StringComparison.Ordinal));
    if (review is null || !review.Role.IsReview)
    {
      return Result.Failure<ChainVerdictOutcome>(new DomainError(ChainReviewCode.NotAReviewStep,
          $"session {reviewerSessionId} holds no review step of chain #{chainId}, so its verdict moves nothing."));
    }

    // ALREADY SETTLED: the verdict was enacted once, and a second call is the same fact.
    if (review.Status is ChainStepStatus.Done or ChainStepStatus.Skipped)
    {
      return Result.Success(new ChainVerdictOutcome(chainId, review.Position,
          ChainReviewDecisionKind.Advance, null, null, "the review step had already settled."));
    }

    Result<ChainReviewRecord?> found = await _reviews
        .FindAsync(chainId, reviewerSessionId, ct).ConfigureAwait(false);
    if (!found.IsSuccess)
    {
      return Result.Failure<ChainVerdictOutcome>(found.Error);
    }

    if (found.Value is not { } verdict)
    {
      return Result.Failure<ChainVerdictOutcome>(new DomainError(ChainReviewCode.NoVerdict,
          "this session's step is a REVIEW step but no review was recorded, so there is no verdict "
          + "to act on. Record it with chain.judge before marking the step done."));
    }

    Result<Chain> settled = await _chains.SettleStepAsync(chainId, review.Position,
        ChainStepStatus.Done, ChainActor.Session(reviewerSessionId),
        ChainReviewNotes.Render(verdict.Findings), now, ct).ConfigureAwait(false);
    return settled.IsSuccess
        ? await DecideAsync(settled.Value, review.Position, verdict, now, ct).ConfigureAwait(false)
        : Result.Failure<ChainVerdictOutcome>(settled.Error);
  }

  /// <summary>The decision and its enactment, split out so the settle's failure path and the
  ///     decision read as separate statements.</summary>
  private async Task<Result<ChainVerdictOutcome>> DecideAsync(Chain chain, int position,
      ChainReviewRecord verdict, DateTimeOffset now, CancellationToken ct)
  {
    ChainStep? target = chain.FixTargetFor(position);
    ChainReviewDecision decision = ChainReviewPolicy.Decide(verdict, target?.Position,
        target?.ReviewAttempts ?? 0);
    return decision.Kind switch
    {
      ChainReviewDecisionKind.SendBack =>
          await SendBackAsync(chain, decision, now, ct).ConfigureAwait(false),
      ChainReviewDecisionKind.ParkAtCap =>
          await ParkAsync(chain, decision, now, ct).ConfigureAwait(false),
      ChainReviewDecisionKind.Advance =>
          await AdvanceAsync(chain, position, now, ct).ConfigureAwait(false),
      ChainReviewDecisionKind.Refused => Result.Failure<ChainVerdictOutcome>(Refused(decision)),
      _ => Result.Failure<ChainVerdictOutcome>(Refused(decision)),
    };
  }

  /// <summary>Why the decision could not be acted on, as the typed failure the caller reports.
  ///     <para>A REFUSAL AND AN UNKNOWN KIND ARE THE SAME ANSWER TO THE CALLER - nothing moved and
  ///     the code names why - so they share one statement rather than a branch each.</para></summary>
  /// <param name="decision">The decision that moved nothing.</param>
  private static DomainError Refused(ChainReviewDecision decision) =>
      decision.Kind == ChainReviewDecisionKind.Refused
          ? new DomainError(decision.Code ?? ChainReviewCode.NoVerdict,
              decision.Reason ?? "the verdict could not be acted on.")
          : new DomainError("UnknownReviewDecision",
              $"{decision.Kind} is not a chain review decision kind; the set is closed.");

  /// <summary>The reviewer failed the work: reopen the judged step and start a fix session seeded
  ///     with the cited findings. The reviewer's cell is replaced by the fix session's.</summary>
  private async Task<Result<ChainVerdictOutcome>> SendBackAsync(Chain chain,
      ChainReviewDecision decision, DateTimeOffset now, CancellationToken ct)
  {
    int target = decision.Target!.Value;
    Result<Chain> reopened = await _chains.ReopenStepAsync(chain.Id, target, ChainActor.Harness,
        decision.ReviewNotes, now, ct).ConfigureAwait(false);
    if (!reopened.IsSuccess)
    {
      return Result.Failure<ChainVerdictOutcome>(reopened.Error);
    }

    ChainStep step = reopened.Value.StepAt(target)!;
    Result<string> started = await ClaimAndStartAsync(reopened.Value, step, now, ct)
        .ConfigureAwait(false);
    return started.IsSuccess
        ? Result.Success(new ChainVerdictOutcome(chain.Id, target,
            ChainReviewDecisionKind.SendBack, target, started.Value, null))
        : Result.Failure<ChainVerdictOutcome>(started.Error);
  }

  /// <summary>The reviewer passed: move the cursor exactly as an ordinary completion would.
  ///     <para>A PASS IS NOT A DIFFERENT KIND OF PROGRESS, so it acquires no rules of its own.
  ///     </para></summary>
  /// <param name="chain">The chain as it stands after the review settled.</param>
  /// <param name="position">The review step's position.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainVerdictOutcome>> AdvanceAsync(Chain chain, int position,
      DateTimeOffset now, CancellationToken ct)
  {
    ChainAdvance next = ChainAdvancePolicy.Next(chain);
    return next.Kind switch
    {
      ChainAdvanceKind.CompleteChain => await CompleteAsync(chain, position, now, ct)
          .ConfigureAwait(false),
      ChainAdvanceKind.StartStep => await StartNextAsync(chain, position, next.Position!.Value,
          now, ct).ConfigureAwait(false),
      ChainAdvanceKind.Blocked or ChainAdvanceKind.Refused or ChainAdvanceKind.NotInAChain
          or ChainAdvanceKind.AlreadySettled => Result.Success(new ChainVerdictOutcome(chain.Id,
              position, ChainReviewDecisionKind.Advance, null, null, next.Reason)),
      _ => Result.Failure<ChainVerdictOutcome>(new DomainError("UnknownAdvanceKind",
          $"{next.Kind} is not a chain advance kind; the set is closed.")),
    };
  }

  /// <summary>Every step settled, so the chain is finished. The chain write comes before any
  ///     close, because the store this runs through belongs to the session being closed.</summary>
  private async Task<Result<ChainVerdictOutcome>> CompleteAsync(Chain chain, int position,
      DateTimeOffset now, CancellationToken ct)
  {
    Result<Chain> completed = await _chains
        .SetStatusAsync(chain.Id, ChainStatus.Completed, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return completed.IsSuccess
        ? Result.Success(new ChainVerdictOutcome(chain.Id, position,
            ChainReviewDecisionKind.Advance, null, null, null))
        : Result.Failure<ChainVerdictOutcome>(completed.Error);
  }

  /// <summary>The step after the review, started exactly as an ordinary advance starts one.</summary>
  private async Task<Result<ChainVerdictOutcome>> StartNextAsync(Chain chain, int position,
      int next, DateTimeOffset now, CancellationToken ct)
  {
    ChainStep step = chain.StepAt(next)!;
    Result<string> started = await ClaimAndStartAsync(chain, step, now, ct).ConfigureAwait(false);
    return started.IsSuccess
        ? Result.Success(new ChainVerdictOutcome(chain.Id, position,
            ChainReviewDecisionKind.Advance, null, started.Value, null))
        : Result.Failure<ChainVerdictOutcome>(started.Error);
  }

  /// <summary>The round-trip cap is spent: park, naming the step. A park is waiting for a human,
  ///     not broken.</summary>
  private async Task<Result<ChainVerdictOutcome>> ParkAsync(Chain chain,
      ChainReviewDecision decision, DateTimeOffset now, CancellationToken ct)
  {
    string reason = decision.Reason ?? "the review round-trip cap was reached.";
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, reason, decision.Target, ChainActor.Harness, now, ct).ConfigureAwait(false);
    return parked.IsSuccess
        ? Result.Success(new ChainVerdictOutcome(chain.Id, decision.Target ?? 0,
            ChainReviewDecisionKind.ParkAtCap, decision.Target, null, reason))
        : Result.Failure<ChainVerdictOutcome>(parked.Error);
  }

  /// <summary>Claims the step, launches its session, and corrects the claim to the real id,
  ///     releasing the claim when the launch fails so the step stays available.
  ///     <para>THE ORDER IS THE ADVANCE'S, for the same reason: the claim is what makes the new
  ///     session's ownership check pass.</para></summary>
  private async Task<Result<string>> ClaimAndStartAsync(Chain chain, ChainStep step,
      DateTimeOffset now, CancellationToken ct)
  {
    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains
        .ClaimStepAsync(chain.Id, step.Position, provisional, now, ct).ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return Result.Failure<string>(claimed.Error);
    }

    Result<string> started = await _launch
        .StartResumedSessionAsync(new ChainStepAssignment(chain.Id, chain.PlanId, step.Position,
            null).WithRole(step.Role), ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      _ = await _chains.ReleaseStepAsync(chain.Id, step.Position, ChainActor.Harness, now, ct)
          .ConfigureAwait(false);
      return started;
    }

    Result<Chain> reassigned = await _chains
        .ReassignStepAsync(chain.Id, step.Position, started.Value, now, ct).ConfigureAwait(false);
    return reassigned.IsSuccess
        ? started
        : Result.Failure<string>(new DomainError(ChainAdvanceCode.ClaimNotCorrected,
            $"chain #{chain.Id} step {step.Position} was started as session {started.Value} but its "
            + $"claim could not be corrected to it ({reassigned.Error.Code}: {reassigned.Error.Message})."));
  }
}
