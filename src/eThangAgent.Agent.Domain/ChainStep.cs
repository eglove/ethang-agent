namespace eThangAgent.AgentDomain;

/// <summary>
/// One ordered step of a chain: the EXECUTION record for a plan step position (issue #300
/// D2/D3). It names the plan step by position and never copies its title or detail — the
/// plan is read at brief time, so a plan edit reaches the next step with no sync.
/// <para>A plan step says what should be true; a chain step says what happened. A <c>Done</c>
/// plan step with three failed chain attempts is a coherent, truthful state, and collapsing
/// the two would lose it.</para>
/// <para>Immutable: every mutation returns a copy. <see cref="ClaimedAt"/> is the claim
/// clock <see cref="ChainClaimPolicy"/> reads; a Running step whose claim has lapsed reads
/// as available through <see cref="EffectiveStatus"/> rather than being rewritten by a
/// sweeper.</para>
/// </summary>
public sealed record ChainStep(
  int Position,
  ChainStepStatus Status,
  string? SessionId,
  int Attempts,
  string? Outcome,
  ChainActor Actor,
  DateTimeOffset? ClaimedAt,
  DateTimeOffset? CompletedAt,
  ChainStepRole Role,
  string? ServedModel = null,
  int ReviewAttempts = 0,
  string? InvalidatedBy = null)
{
  /// <summary>A fresh Pending step with the IMPLEMENTER role (issue #313 D7): the default a
  ///     step takes when nobody assigned one. Use the two-argument overload to assign a
  ///     review step.</summary>
  public static ChainStep Pending(int position) => Pending(position, ChainStepRole.Implement);

  /// <summary>A fresh Pending step in <paramref name="role"/>. The actor starts as the
  ///     harness: nothing has run yet.
  ///     <para>THE ROLE IS PART OF THE RECORD, not a creation-time decoration: a resumed step
  ///     must still know whether it is reviewing or implementing, and a restart reads the row
  ///     rather than a caller's memory.</para></summary>
  public static ChainStep Pending(int position, ChainStepRole role)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    ArgumentNullException.ThrowIfNull(role);
    return new ChainStep(position, ChainStepStatus.Pending, null, 0, null, ChainActor.Harness,
        null, null, role, ServedModel: null, ReviewAttempts: 0);
  }

  /// <summary>True while this step's Running claim is still live. A Running step with NO
  ///     claim clock is treated as LAPSED, not as an eternal claim: the TTL exists so a
  ///     claim cannot outlive its process, and a missing clock is exactly that case.</summary>
  public bool IsClaimLive(DateTimeOffset now)
    => Status == ChainStepStatus.Running
        && ClaimedAt is { } claimed
        && now - claimed < ChainClaimPolicy.ClaimTtl;

  /// <summary>The status a reader should act on: a Running step whose claim lapsed reads as
  ///     <see cref="ChainStepStatus.Pending"/> (available again), every other status is
  ///     itself. Computed on read, never persisted — the stored row keeps the truth about
  ///     what was attempted.</summary>
  public ChainStepStatus EffectiveStatus(DateTimeOffset now)
    => Status == ChainStepStatus.Running && !IsClaimLive(now) ? ChainStepStatus.Pending : Status;

  /// <summary>True when the step may be claimed now: Pending, or a Running step whose claim
  ///     has lapsed. Done, Failed, Skipped and Parked steps are not claimable.</summary>
  public bool IsAvailable(DateTimeOffset now) => EffectiveStatus(now) == ChainStepStatus.Pending;

  /// <summary>Claims this step for a session: Pending (or a lapsed claim) becomes Running,
  ///     the attempt count increments, and the claim clock starts. A claim is an ATTEMPT —
  ///     that is what makes the count bound a session that keeps failing the same way.</summary>
  public ChainStep Claim(string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return this with
    {
      Status = ChainStepStatus.Running,
      SessionId = sessionId.Trim(),
      Attempts = Attempts + 1,
      Actor = ChainActor.Session(sessionId),
      ClaimedAt = now,
      CompletedAt = null,
    };
  }

  /// <summary>Moves a LIVE claim to a different session, keeping the attempt count and the
  ///     claim clock (issue #301). The actor follows the claim: the session now holding it is
  ///     the one that will complete the step, so recording the old claimant would name a
  ///     session that no longer owns the work.</summary>
  public ChainStep Reassign(string sessionId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return this with { SessionId = sessionId.Trim(), Actor = ChainActor.Session(sessionId) };
  }

  /// <summary>Returns this step to Pending without settling it (issue #301): a claim whose
  ///     launch failed, so no session ever ran. The attempt count is KEPT - the launch was
  ///     still an attempt - while the claim clock and the claimant are cleared, because there
  ///     is no live claim any more.</summary>
  public ChainStep Release(ChainActor actor)
  {
    ArgumentNullException.ThrowIfNull(actor);
    return this with
    {
      Status = ChainStepStatus.Pending,
      SessionId = null,
      Actor = actor,
      ClaimedAt = null,
      CompletedAt = null,
    };
  }

  /// <summary>Re-enters a FAILED step for the SAME session (issue #303 D1): the retry
  ///     resumes, it does not restart.
  ///     <para>WHY THIS IS NOT <see cref="Claim"/>: a claim is an ATTEMPT and increments the
  ///     count, and it also accepts a DIFFERENT session. A retry is the same session
  ///     continuing its own work, so the count is left to the caller (which decides whether
  ///     the budget allows another attempt) and the claimant must be unchanged. Re-claiming
  ///     would also restart the claim clock for an attempt that is a continuation.</para>
  ///     <para>REFUSED when the step is not Failed: a retry is only meaningful for work that
  ///     actually broke. A Pending step is claimed, a Done step is finished, and a Parked
  ///     step is waiting for a human - resuming any of them as a retry would misreport what
  ///     happened.</para>
  ///     <para>REFUSED when <paramref name="sessionId"/> is not the session that holds the
  ///     step: a retry resumes THAT session, and a different session has no transcript to
  ///     continue from - it would be a restart wearing a retry's name.</para></summary>
  /// <param name="sessionId">The session being resumed; must be the current claimant.</param>
  /// <param name="now">The clock the new claim window starts at.</param>
  public ChainStep Retry(string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return Status == ChainStepStatus.Failed
      ? Retried(sessionId.Trim(), now)
      : throw new ChainInputException(
          $"step {Position} is {Status}, not Failed; only a failed step can be retried.");
  }

  /// <summary>The re-entered step, refused when a different session tries to resume it.
  ///     Extracted so the two refusals stay separate statements rather than a nested
  ///     conditional (S3358).</summary>
  private ChainStep Retried(string sessionId, DateTimeOffset now)
  {
    return string.Equals(SessionId, sessionId, StringComparison.Ordinal)
      ? this with
      {
        Status = ChainStepStatus.Running,
        Actor = ChainActor.Session(sessionId),
        ClaimedAt = now,
        CompletedAt = null,
      }
      : throw new ChainInputException(
          $"step {Position} was run by session {SessionId ?? "(none recorded)"}, not {sessionId}; "
          + "a retry resumes the SAME session, because that is the transcript the work continues from.");
  }

  /// <summary>Reopens a DONE step so a fix session can work on it again (issue #320): the
  ///     review back-edge's own transition, and the reason it is not <see cref="Claim"/>.
  ///     <para>WHY NOT A CLAIM: a claim is an ATTEMPT on work nobody has done yet, and it
  ///     increments the retry counter. A reopen is a reviewer's verdict sending finished work
  ///     back, so the RETRY counter is untouched (the session did not break) and the REVIEW
  ///     counter increments instead — the two budgets Q14 keeps separate.</para>
  ///     <para>REFUSED WHEN THE STEP IS NOT DONE. The only thing a review reopens is work the
  ///     chain believed was finished; a Pending step has nothing to redo, a Failed one is
  ///     #303's retry path, and a Parked one is already waiting for a human.</para>
  ///     <para>THE CLAIMANT IS CLEARED and the outcome is kept as the review's own record of
  ///     what it found: the reopened step is available to whichever session the harness starts
  ///     for it, and the previous outcome stays readable until that session settles it again.
  ///     </para></summary>
  /// <param name="outcome">What the review recorded, so the reopened step says why.</param>
  /// <param name="actor">Who reopened it — the harness, on the reviewer's verdict.</param>
  public ChainStep Reopen(ChainActor actor, string? outcome)
  {
    ArgumentNullException.ThrowIfNull(actor);
    return Status == ChainStepStatus.Done
        ? this with
        {
          Status = ChainStepStatus.Pending,
          SessionId = null,
          Outcome = outcome,
          Actor = actor,
          ClaimedAt = null,
          CompletedAt = null,
          ReviewAttempts = ReviewAttempts + 1,
        }
        : throw new ChainInputException(
            $"step {Position} is {Status}, not Done; only a step the chain believed was finished "
            + "can be reopened by a review.");
  }

  /// <summary>True when this step's review round-trip budget is spent (issue #320).
  ///     <para>Computed from the count rather than stored, so the bound has one home
  ///     (<see cref="ChainReviewPolicy.MaxReviewAttempts"/>) and cannot drift from a
  ///     persisted copy of itself.</para></summary>
  public bool ReviewBudgetSpent => !ChainReviewPolicy.CanSendBack(ReviewAttempts);

  /// <summary>Records the model this step's session actually SERVED on (issue #218).
  ///     <para>WHY THE STEP CARRIES IT: the separation rule compares a reviewer's model to the
  ///     IMPLEMENTER's, and the implementer's session is closed by the time the review runs. The
  ///     chain record is the durable answer (D3) - a live parent's configuration is neither
  ///     available after a restart nor authoritative about what ran.</para>
  ///     <para>NULL MEANS 'NOT RECORDED', never 'no model'. A step that settled before this field
  ///     existed carries none, and the rule treats that as 'nothing to compare against' rather
  ///     than fabricating a match.</para></summary>
  /// <param name="modelId">The model id the session served on, or null to clear it.</param>
  public ChainStep WithServedModel(string? modelId) =>
      this with { ServedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim() };

  /// <summary>True when a later refutation disproved something this step concluded (issue
  ///     #304 D5).
  ///     <para>THE MARKER DOES NOT CHANGE THE STATUS, and that is the whole point: the step's work
  ///     genuinely was done, so rewriting it to Pending would be a lie about what happened, and
  ///     re-running it may not help. Both facts survive - it was done, and its conclusion no
  ///     longer holds - which is the truthful record.</para></summary>
  public bool IsInvalidated => InvalidatedBy is not null;

  /// <summary>Marks this step INVALIDATED by a refutation, keeping its status (issue #304 D5).
  ///     <para>SET BY THE FALSIFICATION, NEVER BY A HUMAN OR AN AGENT. The caller is the
  ///     refutation's own record site, so an agent cannot un-invalidate its own disproof and
  ///     cannot invalidate a peer's step by hand - <see cref="ClearInvalidation"/> is the only
  ///     way back, and that one is the human's.</para>
  ///     <para>IDEMPOTENT AND FIRST-WINS: a second refutation against an already-invalidated
  ///     step keeps the FIRST marker, because the completion refusal names one refutation and
  ///     the earlier disproof is the one that made the claim untrue.</para></summary>
  /// <param name="refutationKey">The refutation record that did it, so a refusal can name it.</param>
  public ChainStep Invalidate(string refutationKey)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(refutationKey);
    return IsInvalidated ? this : this with { InvalidatedBy = refutationKey.Trim() };
  }

  /// <summary>Clears the invalidation marker (issue #304 D5). A HUMAN DECISION, which is the
  ///     correct place for it: the marker says a conclusion no longer holds, and only someone
  ///     who can weigh the new evidence may say it holds again.</summary>
  public ChainStep ClearInvalidation() => this with { InvalidatedBy = null };

  /// <summary>Records this step's outcome. <paramref name="status"/> must be one of the
  ///     SETTLED step statuses (Done, Failed, Skipped, Parked); a chain step does not move
  ///     back to Pending or Running through an outcome, it is claimed or retried instead.</summary>
  public ChainStep Settle(ChainStepStatus status, ChainActor actor, string? outcome, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(actor);
    return status is ChainStepStatus.Done or ChainStepStatus.Failed
            or ChainStepStatus.Skipped or ChainStepStatus.Parked
      ? this with
      {
        Status = status,
        Outcome = outcome,
        Actor = actor,
        ClaimedAt = null,
        CompletedAt = now,
      }
      : throw new ArgumentException(
        $"'{status}' is not a settled chain step status; settle with Done, Failed, Skipped, or Parked.",
        nameof(status));
  }
}
