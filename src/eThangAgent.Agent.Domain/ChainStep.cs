namespace eThangAgent.AgentDomain;

/// <summary>
/// One ordered step of a chain: the EXECUTION record for a plan step position (issue #300
/// D2/D3). It names the plan step by position and never copies its title or detail — the
/// plan is read at brief time, so a plan edit reaches the next step with no sync.
/// <para>A plan step says what should be true; a chain step says what happened. A <c>Done</c>
/// plan step with three failed chain attempts is a coherent, truthful state, and collapsing
/// the two would lose it.</para>
/// <para>Immutable: every mutation returns a copy. A claim has NO time limit: a Running
/// step holds its claim until it is settled, released, or reassigned, and nothing expires
/// it on a clock. <see cref="ClaimedAt"/> records when the claim started, for the record
/// and for telemetry durations.</para>
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
  string? InvalidatedBy = null,
  int? Group = null,
  string? WorktreePath = null,
  string? WorktreeBranch = null)
{
  /// <summary>A fresh Pending step with the IMPLEMENTER role (issue #313 D7): the default a
  ///     step takes when nobody assigned one. Use the two-argument overload to assign a
  ///     review step.</summary>
  public static ChainStep Pending(int position) => Pending(position, ChainStepRole.Implement);

  /// <summary>A fresh Pending step in <paramref name="role"/>, optionally in a group. The
  ///     actor starts as the harness: nothing has run yet.
  ///     <para>THE ROLE IS PART OF THE RECORD, not a creation-time decoration: a resumed step
  ///     must still know whether it is reviewing or implementing, and a restart reads the row
  ///     rather than a caller's memory.</para></summary>
  /// <param name="position">The 1-based plan step position.</param>
  /// <param name="role">The role assigned to the session that runs it.</param>
  /// <param name="group">The group the step runs in, or null when it runs alone.</param>
  public static ChainStep Pending(int position, ChainStepRole role, int? group = null)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    ArgumentNullException.ThrowIfNull(role);
    return new ChainStep(position, ChainStepStatus.Pending, null, 0, null, ChainActor.Harness,
        null, null, role, InvalidatedBy: null, Group: group);
  }

  /// <summary>True when the step may be claimed: only a Pending step is. A Running step is
  ///     held by its claimant however long it takes; Done, Failed, Skipped and Parked steps
  ///     are not claimable at all.</summary>
  public bool IsAvailable => Status == ChainStepStatus.Pending;

  /// <summary>Claims this step for a session: Pending becomes Running, the attempt count
  ///     increments, and the claim clock starts. A claim is an ATTEMPT — that is what makes
  ///     the count bound a session that keeps failing the same way.</summary>
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

  /// <summary>The prefix the harness claims a step under before the session's id exists.
  ///     <para>A PLACEHOLDER IS A GUESS ABOUT WHO WILL OWN THE STEP, never an owner: the real
  ///     id is written by <see cref="Reassign"/> once the launch answers. A step still holding
  ///     one is a claim whose correction never landed.</para></summary>
  public const string PlaceholderPrefix = "pending-";

  /// <summary>True when <paramref name="sessionId"/> is the provisional spelling the harness
  ///     claims under, rather than a session that could complete the step.</summary>
  /// <param name="sessionId">The claimant to classify, or null.</param>
  public static bool IsPlaceholderClaimant(string? sessionId)
    => sessionId is not null
        && sessionId.StartsWith(PlaceholderPrefix, StringComparison.Ordinal);

  /// <summary>True when this step's claim was never corrected to a real session.</summary>
  public bool HoldsPlaceholderClaim => IsPlaceholderClaimant(SessionId);

  /// <summary>Corrects a PLACEHOLDER claim to the session that actually runs the step.
  ///     <para>WHY THIS IS NOT <see cref="Reassign"/>: a reassign moves a live claim between
  ///     two real sessions. This is the narrower act of replacing a guess with the fact, and it
  ///     is the only correction a stranded step needs.</para>
  ///     <para>REFUSED FOR A REAL CLAIMANT. A session id that is not a placeholder names a
  ///     session that holds the step; adopting over it would take work from the session doing
  ///     it. Idempotent for the claimant already recorded, because adoption runs mechanically at
  ///     every session open - including the ones that are already correct.</para></summary>
  /// <param name="sessionId">The real session that owns the step.</param>
  public ChainStep Adopt(string sessionId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return Adoptable(sessionId.Trim())
      ? this with { SessionId = sessionId.Trim(), Actor = ChainActor.Session(sessionId) }
      : throw new ChainInputException(NotAdoptable());
  }

  /// <summary>True when <paramref name="sessionId"/> may take this step's claim: it already
  ///     holds it, or the claim is a placeholder.</summary>
  private bool Adoptable(string sessionId)
    => Status == ChainStepStatus.Running
        && (string.Equals(SessionId, sessionId, StringComparison.Ordinal) || HoldsPlaceholderClaim);

  /// <summary>Why this step cannot be adopted, naming the claimant that would be displaced.</summary>
  private string NotAdoptable() => Status != ChainStepStatus.Running
    ? $"step {Position} is {Status}; only a running step's claim can be adopted."
    : $"step {Position} is held by session {SessionId}; only a placeholder claim can be adopted, "
        + $"because a real claimant is the session doing the work.";

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
  /// <summary>Records the git worktree this step's session runs in and the branch checked out
  ///     there, or clears both with null.
  ///     <para>ONLY A GROUP MEMBER HAS ONE: a sequential step runs in the workspace checkout, so
  ///     null is the ordinary case and means 'no worktree', never 'not recorded'.</para>
  ///     <para>THE BRANCH IS STORED, NOT DERIVED FROM THE PATH. The reconciler merges by branch,
  ///     and a name derived from a directory is a second source of truth that can disagree with
  ///     what git actually checked out.</para></summary>
  /// <param name="path">The worktree directory, or null to clear it.</param>
  /// <param name="branch">The branch checked out in that worktree, or null to clear it.</param>
  public ChainStep WithWorktree(string? path, string? branch) =>
      this with
      {
        WorktreePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
        WorktreeBranch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
      };

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
