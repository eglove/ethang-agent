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
  IReadOnlyList<ChainStep> Steps)
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
            [.. ordered.Select(seed => ChainStep.Pending(seed.Position, seed.Role))]);
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

  /// <summary>Claims the step at the 1-based position for a session. The step must be
  ///     available: Pending, or Running with a LAPSED claim (<see cref="ChainClaimPolicy"/>).</summary>
  public Chain ClaimStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    ChainStep step = Steps[index];
    return step.IsAvailable(now)
      ? ReplaceStep(index, step.Claim(sessionId, now), now)
      : throw new ChainInputException(
          $"chain #{Id} step {position} is {step.EffectiveStatus(now)}; only a pending step (or one whose claim has lapsed) can be claimed.");
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
  ///     no new attempt was made and the claim never lapsed. Re-claiming would double-count
  ///     an attempt the bound (#303 D2) reads.</para>
  ///     <para>Refused when the step is not Running or its claim has lapsed: a lapsed claim is
  ///     available to anyone, and silently reassigning it would steal it from a claimant that
  ///     may already be working.</para></summary>
  public Chain ReassignStep(int position, string sessionId, DateTimeOffset now)
  {
    EnsureMutable();
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    int index = IndexOf(position);
    ChainStep step = Steps[index];
    return step.Status == ChainStepStatus.Running && step.IsClaimLive(now)
      ? ReplaceStep(index, step.Reassign(sessionId), now)
      : throw new ChainInputException(
          $"chain #{Id} step {position} is {step.EffectiveStatus(now)}; only a live claim can be reassigned.");
  }

  /// <summary>Records the model a step's session actually served on (issue #218).
  ///     <para>WHY THE CHAIN OWNS THIS WRITE: the value is a fact about the step's SESSION, and
  ///     the chain is the record of what each step did. The implementer's model must outlive its
  ///     session (D3), so it is stamped on the step rather than read back from a live parent that
  ///     is closed by the time the review runs.</para>
  ///     <para>Deliberately NOT part of <see cref="ClaimStep"/>: the model is not known until the
  ///     session exists and resolves one, which is AFTER the claim. Claiming is an attempt; this
  ///     is what the attempt ran on.</para></summary>
  /// <param name="position">The 1-based step position.</param>
  /// <param name="modelId">The model id the session served on, or null to clear it.</param>
  /// <param name="now">The clock.</param>
  public Chain RecordServedModel(int position, string? modelId, DateTimeOffset now)
  {
    EnsureMutable();
    int index = IndexOf(position);
    return ReplaceStep(index, Steps[index].WithServedModel(modelId), now);
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

  /// <summary>The first step a session may claim now, or null when none is available.</summary>
  public ChainStep? NextAvailableStep(DateTimeOffset now)
    => Steps.FirstOrDefault(step => step.IsAvailable(now));

  /// <summary>True when no step is Pending or Running (a lapsed claim counts as available,
  ///     so a stale Running step keeps the chain unfinished — the honest reading).</summary>
  public bool IsSettled(DateTimeOffset now)
    => Steps.All(step => !step.IsAvailable(now)
        && step.EffectiveStatus(now) != ChainStepStatus.Running);

  /// <summary>Finds a step by its 1-based position, or null.</summary>
  public ChainStep? StepAt(int position) => Steps.FirstOrDefault(step => step.Position == position);

  /// <summary>Drops the park facts when the chain leaves Parked: a chain that is running or
  ///     finished is no longer waiting on anything, and a stale reason would read as one.</summary>
  private static Chain ClearParkFields(Chain chain, ChainStatus target)
    => target == ChainStatus.Parked
      ? chain
      : chain with { ParkReason = null, ParkedAtPosition = null };

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
