using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>
/// Aggregate root for chains: the durable execution record the harness owns (issue #300).
/// A chain is a SIBLING of the plan, never a field set on it — the plan's writer is a model
/// judging what is done, the chain's writer is the harness advancing a cursor, and merging
/// them would give two writers one CAS version with no way to tell which refusal came from
/// whom.
/// <para>The chain REFERENCES the plan (id, plus the version it started at) and never copies
/// it: titles and details are read from the plan at brief time, so a plan edit reaches the
/// next step with no sync, while the recorded start version keeps a later revision VISIBLE
/// without being retroactive.</para>
/// <para>Immutable: every mutation returns a copy. Terminal statuses (Completed / Abandoned)
/// freeze the chain; frozen mutation and unknown positions surface as
/// <see cref="ChainInputException"/>. Persistence uses the same compare-and-swap contract
/// the plan aggregate does: a save lands only while the stored version still matches.</para>
/// </summary>
public sealed record Chain(
  int Id,
  string WorkspaceId,
  int PlanId,
  int PlanVersion,
  string Title,
  ChainStatus Status,
  string? ParkReason,
  int? ParkedAtPosition,
  string? SessionId,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  int Version,
  IReadOnlyList<ChainStep> Steps,
  string? FinalSessionId = null)
{
  /// <summary>Creates a fresh Running chain with an unassigned id (0) and version 0.
  ///     <para>THE SEEDS ARE THE DECLARATION (issue #313 D2): each names a plan position and
  ///     the role assigned to the session that will run it. A position-only overload exists
  ///     for the many call sites that assign no role, and it means 'implement' - stated once,
  ///     here, rather than at each of them.</para></summary>
  public static Chain New(string workspaceId, int planId, int planVersion, string title,
      string? sessionId, IReadOnlyList<int> stepPositions, DateTimeOffset now)
      => NewFromSeeds(workspaceId, planId, planVersion, title, sessionId,
          [.. (stepPositions ?? throw new ArgumentNullException(nameof(stepPositions)))
              .Select(ChainStepSeed.Implementing)], now);

  /// <summary>Creates a fresh Running chain from declared step seeds.
  ///     <para>NAMED, NOT AN OVERLOAD. An overload differing from the position-only factory
  ///     only by element type makes a collection expression ambiguous, and a caller reading
  ///     <c>Chain.New(..., [1, 2], now)</c> against two signatures has to resolve it by
  ///     inference. Two names say which one is meant.</para>
  ///     <para>AN EMPTY CHAIN IS REFUSED (D10). A chain that immediately reports Completed is
  ///     a lie about work that did not happen: a plan with no executable step is a plan
  ///     defect, not a record. The refusal lives in the aggregate because it is a property of
  ///     what a chain IS, not of one caller's validation.</para>
  ///     <para>POSITIONS ARE ORDERED AND DEDUPLICATED, so the record's step order is the
  ///     plan's order and a caller cannot state one position twice. A position that appears
  ///     twice with DIFFERENT roles is a contradiction the aggregate refuses rather than
  ///     silently resolving.</para></summary>
  public static Chain NewFromSeeds(string workspaceId, int planId, int planVersion, string title,
      string? sessionId, IReadOnlyList<ChainStepSeed> seeds, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    ArgumentOutOfRangeException.ThrowIfLessThan(planId, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(planVersion, 1);
    ArgumentNullException.ThrowIfNull(seeds);

    List<ChainStepSeed> ordered = [.. seeds
        .OrderBy(seed => seed.Position)
        .GroupBy(seed => seed.Position)
        .Select(OneSeedPerPosition)];
    return ordered.Count == 0
        ? throw new ChainInputException("a chain needs at least one executable step position.")
        : new Chain(0, workspaceId.Trim(), planId, planVersion, title.Trim(), ChainStatus.Running,
            null, null, sessionId, now, now, 0,
            [.. ordered.Select(seed => ChainStep.Pending(seed.Position, seed.Role, seed.Group))]);
  }

  /// <summary>One seed per position: a duplicate that agrees collapses to one, and a
  ///     duplicate that DISAGREES is a contradiction - two roles for one step is a chain
  ///     nobody can start correctly, so it is refused by name rather than resolved by
  ///     ordering.</summary>
  private static ChainStepSeed OneSeedPerPosition(IGrouping<int, ChainStepSeed> group)
  {
    ChainStepSeed first = group.First();
    return group.All(seed => seed.Role == first.Role)
      ? first
      : throw new ChainInputException(
          $"step position {first.Position} was declared with two different roles "
          + $"({first.Role.Kind} and {group.First(seed => seed.Role != first.Role).Role.Kind}); "
          + "a chain step has exactly one role.");
  }

  /// <summary>Moves the chain's status; the legal moves and the completion invariant live in
  ///     <see cref="ChainStatusTransition"/>. The actor is recorded because every transition
  ///     names who made it — including "the harness", which has no session.</summary>
  public Chain SetStatus(ChainStatus target, ChainActor actor, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentNullException.ThrowIfNull(actor);
    Violation? violation = new ChainStatusTransition(target).ViolationFor(this);
    return violation is not null
      ? throw new ChainInputException(violation.Message)
      : ClearParkFields(this with { Status = target, UpdatedAt = now }, target);
  }

  /// <summary>Parks the chain, naming WHY and WHICH step stopped it (issue #303 D9): a parked
  ///     chain is waiting for a human, a failed one is broken, and the two must not look
  ///     alike. Parking is a first-class status, never an error.</summary>
  public Chain Park(string reason, int? position, ChainActor actor, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentNullException.ThrowIfNull(actor);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    if (position is { } p)
    {
      _ = IndexOf(p);
    }

    Violation? violation = new ChainStatusTransition(ChainStatus.Parked).ViolationFor(this);
    return violation is not null
      ? throw new ChainInputException(violation.Message)
      : this with
      {
        Status = ChainStatus.Parked,
        ParkReason = reason.Trim(),
        ParkedAtPosition = position,
        UpdatedAt = now,
      };
  }

  /// <summary>Un-parks the chain: Parked -> Running. The park reason is cleared, because a
  ///     resumed chain is no longer waiting for anything.</summary>
  public Chain Resume(ChainActor actor, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(actor);
    return SetStatus(ChainStatus.Running, actor, now) with { ParkReason = null, ParkedAtPosition = null };
  }

  /// <summary>Un-parks the chain AND re-arms the step that stopped it, so the chain can actually
  ///     move again (issue #303's missing exit).
  ///     <para>WHY THIS IS NOT <see cref="Resume"/>. Moving Parked back to Running is half of
  ///     what a stuck chain needs. The step the park named is almost always Failed or Parked
  ///     itself - it is what stopped the chain - and a chain whose status says Running while its
  ///     blocking step is unstartable reports work in progress that cannot progress. The advance
  ///     finds no available step, blocks, and parks the chain again on the next completion: the
  ///     user's resume appears to do nothing.</para>
  ///     <para>THE STEP GOES BACK TO PENDING, NOT TO RUNNING. Its session is gone or broken - the
  ///     attempt budget is spent, or the work was refused - so there is no transcript to
  ///     continue. Pending makes it available to the advance, which starts a FRESH session for
  ///     it. The attempt count is cleared with it: a resume is a human's decision that this work
  ///     should be tried again, and re-entering a chain only to fail on the spent budget the park
  ///     was about would make the exit a loop.</para>
  ///     <para>INVALIDATION MARKERS ARE CLEARED TOO. They are the other way a chain becomes
  ///     uncompletable (#304 D5), and a resume is the same human decision about both: 'this work
  ///     should proceed'. Clearing them here keeps the exit in one place rather than requiring
  ///     two acts a reader has to discover.</para></summary>
  /// <param name="actor">Who resumed it.</param>
  /// <param name="now">The clock.</param>
  public Chain ResumeAndRearm(ChainActor actor, DateTimeOffset now)
  {
    ArgumentNullException.ThrowIfNull(actor);
    Chain resumed = ClearInvalidation(now);
    return resumed.ParkedAtPosition is { } stopped && resumed.StepAt(stopped) is { } step
      ? resumed.ReplaceStep(resumed.IndexOf(stopped), Rearmed(step), now) with
      {
        Status = ChainStatus.Running,
        ParkReason = null,
        ParkedAtPosition = null,
      }
      : resumed with { Status = ChainStatus.Running, ParkReason = null, ParkedAtPosition = null };
  }

  /// <summary>The step a resume re-arms: back to Pending, claim and outcome cleared, and the
  ///     attempt count reset. A step that is already available is left exactly as it is.
  ///     <para>A DONE STEP IS LEFT ALONE TOO. Re-arming finished work would make a resume redo
  ///     it, and the chain only parks on a step that did not finish - the park names the step
  ///     that stopped it.</para></summary>
  /// <param name="step">The step the park named.</param>
  private static ChainStep Rearmed(ChainStep step) =>
      step.Status is ChainStepStatus.Failed or ChainStepStatus.Skipped or ChainStepStatus.Parked
        ? step with
        {
          Status = ChainStepStatus.Pending,
          SessionId = null,
          Attempts = 0,
          Outcome = null,
          ClaimedAt = null,
          CompletedAt = null,
          Actor = ChainActor.Harness,
        }
        : step;

  /// <summary>Claims the step at the 1-based position for a session. Only a Pending step
  ///     can be claimed: a Running step is held by its claimant until it is settled,
  ///     released, or reassigned.</summary>
  public Chain ClaimStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    ChainStep step = Steps[index];
    return step.IsAvailable
      ? ReplaceStep(index, step.Claim(sessionId, now), now)
      : throw new ChainInputException(
          $"chain #{Id} step {position} is {step.Status}; only a pending step can be claimed.");
  }

  /// <summary>Settles the step at the 1-based position with an outcome.</summary>
  public Chain SettleStep(int position, ChainStepStatus status, ChainActor actor, string? outcome,
      DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].Settle(status, actor, outcome, now), now);
  }

  /// <summary>Moves a LIVE claim to a different session without spending another attempt
  ///     (issue #301). The harness claims a step before it can know the new session's id —
  ///     the id does not exist until the session does — so the placeholder claim is corrected
  ///     here once the session is real.
  ///     <para>Deliberately NOT a second <see cref="ClaimStep"/>: claiming increments
  ///     <see cref="ChainStep.Attempts"/> and restarts the claim clock, and neither is true —
  ///     no new attempt was made. Re-claiming would double-count an attempt the bound
  ///     (#303 D2) reads.</para>
  ///     <para>Refused when the step is not Running: only a held claim belongs to a session,
  ///     and reassigning a Pending step would record a claimant that never claimed it.</para></summary>
  public Chain ReassignStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    int index = IndexOf(position);
    ChainStep step = Steps[index];
    return step.Status == ChainStepStatus.Running
      ? ReplaceStep(index, step.Reassign(sessionId), now)
      : throw new ChainInputException(
          $"chain #{Id} step {position} is {step.Status}; only a running step's claim can be reassigned.");
  }

  /// <summary>Drops the chain: a terminal status for work that is being abandoned rather than
  ///     finished.
  ///     <para>THE EXIT A CHAIN OTHERWISE DOES NOT HAVE. <c>resume-parked</c> refuses a chain that
  ///     is Running, no session can complete a step it does not hold, and a chain whose claim is
  ///     stranded therefore cannot be moved by anything. Abandoning is the honest exit: the record
  ///     says the work was dropped, which is true, instead of saying it is in progress forever.</para>
  ///     <para>IT REFUSES A FINISHED CHAIN, because Abandoned and Completed are both terminal and a
  ///     chain cannot stop twice - the move would erase the fact that the work landed.</para>
  ///     <para>THE REASON IS REQUIRED. A chain marked Abandoned with no explanation is a record
  ///     nobody can act on, and the next reader cannot tell a deliberate drop from a defect.</para>
  ///     </summary>
  /// <param name="reason">Why the work is being dropped.</param>
  /// <param name="now">The clock.</param>
  public Chain Abandon(string reason, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    Violation? violation = new ChainStatusTransition(ChainStatus.Abandoned).ViolationFor(this);
    return violation is not null
      ? throw new ChainInputException(violation.Message)
      : this with
      {
        Status = ChainStatus.Abandoned,
        ParkReason = reason.Trim(),
        ParkedAtPosition = null,
        UpdatedAt = now,
      };
  }

  /// <summary>Corrects a PLACEHOLDER claim on the step at <paramref name="position"/> to the
  ///     session that actually runs it.
  ///     <para>THE HARNESS CLAIMS BEFORE IT CAN KNOW THE SESSION, so every claim is written
  ///     twice: once under a provisional id to hold the step during the launch, once as the real
  ///     id once the launch answers. This is the second write as an act of its own, so a step left
  ///     on the placeholder can be repaired without inventing an attempt or a status.</para>
  ///     <para>A REAL CLAIMANT IS AN OWNER and is never displaced - see
  ///     <see cref="ChainStep.Adopt"/>, which owns that rule.</para></summary>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="sessionId">The real session that owns the step.</param>
  /// <param name="now">The clock the record write is stamped with.</param>
  public Chain AdoptStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].Adopt(sessionId), now);
  }

  /// <summary>Anchors the step at <paramref name="position"/> to the git worktree its session
  ///     runs in, and to the branch checked out there.</summary>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="path">The worktree directory, or null to clear it.</param>
  /// <param name="branch">The branch checked out in that worktree, or null to clear it.</param>
  /// <param name="now">The clock.</param>
  public Chain WithWorktree(int position, string? path, string? branch, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].WithWorktree(path, branch), now);
  }

  /// <summary>Returns a claimed step to Pending WITHOUT settling it (issue #301): the launch
  ///     that claimed it failed, so no session ever existed to run it.
  ///     <para>The ATTEMPT COUNT IS KEPT. A launch that failed is still an attempt the bound
  ///     must see, or a step whose every launch fails would retry forever; what is released is
  ///     the claim, not the history.</para>
  ///     <para>Distinct from settling it <c>Failed</c>: nothing ran, so there is no outcome to
  ///     record and the step is still available to the next advance.</para></summary>
  public Chain ReleaseStep(int position, ChainActor actor, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentNullException.ThrowIfNull(actor);
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].Release(actor), now);
  }

  /// <summary>Re-enters the FAILED step at <paramref name="position"/> for the session that
  ///     ran it (issue #303 D1): a retry resumes, it does not restart.
  ///     <para>THE SAME SESSION, ENFORCED BY THE STEP: <see cref="ChainStep.Retry"/> refuses a
  ///     different claimant, because a different session has no transcript to continue from.</para>
  ///     <para>THE ATTEMPT COUNT IS NOT TOUCHED. A claim is an attempt and increments the count;
  ///     a retry is the same session continuing, so the count already reflects this attempt.
  ///     The BUDGET decision (<see cref="ChainRetryPolicy.Decide"/>) is the caller's, because
  ///     only the caller knows whether it is retrying or parking.</para></summary>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="sessionId">The session being resumed; must be the current claimant.</param>
  /// <param name="now">The clock the new claim window starts at.</param>
  public Chain RetryStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].Retry(sessionId, now), now);
  }

  /// <summary>The nearest PRECEDING step a later conclusion supersedes: the work a refutation
  ///     recorded by the step at <paramref name="position"/> is about.
  ///     <para>WHY THE NEAREST PRECEDING STEP AND NOT A DECLARED PAIRING: chain steps are
  ///     INDEPENDENT (#299 D8), so the record carries no link from a finding to the step it
  ///     disproves, and the honest reading is that a refutation is about the work that came
  ///     before it.</para>
  ///     <para>ONLY A STEP THAT ALREADY RAN CAN BE INVALIDATED, so the search skips anything
  ///     that is not Done: a Pending step concluded nothing to disprove.</para></summary>
  /// <param name="position">The position of the step recording the refutation.</param>
  public ChainStep? FixTargetFor(int position)
    => Steps
        .Where(candidate => candidate.Position < position
            && candidate.Status == ChainStepStatus.Done)
        .OrderByDescending(candidate => candidate.Position)
        .FirstOrDefault();

  /// <summary>Marks the step at <paramref name="position"/> INVALIDATED by a refutation,
  ///     keeping its status (issue #304 D5).
  ///     <para>THE REFUTATION IS THE ACTOR, not a session and not a human: a later step's ledger
  ///     disproved something this step concluded, and the marker records that fact rather than
  ///     anyone's decision. That is why this mutation takes no <see cref="ChainActor"/> - the
  ///     other mutations do because a person or a session chose them.</para>
  ///     <para>THE CHAIN'S OWN STATUS IS UNTOUCHED. An invalidated step makes the chain
  ///     UNCOMPLETABLE (see <see cref="ChainStatusTransition"/>), but it does not park it: the
  ///     remaining steps can still run, and parking would stop work that is still valid.
  ///     </para></summary>
  /// <param name="position">The 1-based step position to invalidate.</param>
  /// <param name="refutationKey">The refutation record that did it.</param>
  /// <param name="now">The clock.</param>
  public Chain InvalidateStep(int position, string refutationKey, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].Invalidate(refutationKey), now);
  }

  /// <summary>Clears every invalidation marker (issue #304 D5). THE HUMAN'S EXIT: a person who
  ///     has weighed the refutation and decided the conclusion still holds says so here, and
  ///     the chain may complete again.
  ///     <para>CLEARS ALL OF THEM rather than one by position, because the decision being
  ///     recorded is 'these conclusions stand' - a per-step clear would be a way to walk a chain
  ///     into completion one marker at a time, which is the shape D5 exists to prevent.
  ///     </para></summary>
  /// <param name="now">The clock.</param>
  public Chain ClearInvalidation(DateTimeOffset now)
  {
    EnsureMutable();
    return Steps.Any(step => step.IsInvalidated)
      ? this with { Steps = [.. Steps.Select(step => step.ClearInvalidation())], UpdatedAt = now }
      : this;
  }

  /// <summary>The steps a refutation has invalidated, in position order. What the completion
  ///     refusal names.</summary>
  public IReadOnlyList<ChainStep> InvalidatedSteps =>
      [.. Steps.Where(step => step.IsInvalidated).OrderBy(step => step.Position)];

  /// <summary>The first step a session may claim, or null when none is available.
  ///     <para>A GROUP IS A BARRIER: every member of a group is offered while the group is
  ///     unsettled, so the members run together, and a step is offered only once every group
  ///     that owns an earlier position has settled.</para></summary>
  public ChainStep? NextAvailableStep() => Steps.FirstOrDefault(step =>
      step.IsAvailable && !HasUnsettledGroupBefore(step));

  /// <summary>The steps of <paramref name="group"/> in position order.</summary>
  /// <param name="group">The group id.</param>
  public IReadOnlyList<ChainStep> GroupMembers(int group) =>
      [.. Steps.Where(step => step.Group == group).OrderBy(step => step.Position)];

  /// <summary>True when no member of <paramref name="group"/> is Pending or Running. A group
  ///     with no members is settled.</summary>
  /// <param name="group">The group id.</param>
  public bool GroupIsSettled(int group) =>
      !GroupMembers(group).Any(step => step.Status is ChainStepStatus.Pending or ChainStepStatus.Running);

  /// <summary>The group the step at <paramref name="position"/> belongs to, or null when it
  ///     runs alone.</summary>
  /// <param name="position">The 1-based step position.</param>
  public int? GroupAt(int position) => StepAt(position)?.Group;

  /// <summary>True when no step is Pending or Running: a claimed step keeps the chain
  ///     unfinished however long its session takes.</summary>
  public bool IsSettled()
    => Steps.All(step => step.Status is not (ChainStepStatus.Pending or ChainStepStatus.Running));

  /// <summary>Finds a step by its 1-based position, or null.</summary>
  public ChainStep? StepAt(int position) => Steps.FirstOrDefault(step => step.Position == position);

  /// <summary>Records the final agent's session id. The final agent is not a step, so it has no
  ///     claim to record; this is the durable link the completion path writes once the final
  ///     session exists.</summary>
  /// <param name="sessionId">The final agent's session id.</param>
  /// <param name="now">The clock.</param>
  public Chain WithFinalSession(string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return this with { FinalSessionId = sessionId.Trim(), UpdatedAt = now };
  }

  /// <summary>Drops the park facts when the chain leaves Parked: a chain that is running or
  ///     finished is no longer waiting on anything, and a stale reason would read as one.</summary>
  private static Chain ClearParkFields(Chain chain, ChainStatus target)
    => target == ChainStatus.Parked
      ? chain
      : chain with { ParkReason = null, ParkedAtPosition = null };

  /// <summary>True when a group OTHER than the step's own owns a position earlier than the
  ///     step's and has not settled. A member's own group is not a barrier to itself: that is
  ///     what lets the members of a group start together.</summary>
  private bool HasUnsettledGroupBefore(ChainStep step) =>
      Steps.Any(candidate => candidate.Position < step.Position
          && candidate.Group is { } group
          && group != step.Group
          && !GroupIsSettled(group));

  private Chain ReplaceStep(int index, ChainStep step, DateTimeOffset now)
  {
    List<ChainStep> steps = [.. Steps];
    steps[index] = step;
    return this with { Steps = steps, UpdatedAt = now };
  }

  private int IndexOf(int position)
  {
    for (int i = 0; i < Steps.Count; i++)
    {
      if (Steps[i].Position == position)
      {
        return i;
      }
    }

    throw new ChainInputException(
        $"unknown chain step position {position}; the chain has {Steps.Count} step(s).");
  }

  private void EnsureMutable()
  {
    if (Status is ChainStatus.Running or ChainStatus.Parked)
    {
      return;
    }

    string status = Status == ChainStatus.Completed ? "completed" : "abandoned";
    throw new ChainInputException($"chain #{Id} is {status}; terminal chains are frozen.");
  }
}
