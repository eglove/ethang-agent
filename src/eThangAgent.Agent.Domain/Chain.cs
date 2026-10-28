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
  /// <summary>Creates a fresh Running chain with an unassigned id (0) and version 0.</summary>
  public static Chain New(string workspaceId, int planId, int planVersion, string title,
      string? sessionId, IReadOnlyList<int> stepPositions, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    ArgumentOutOfRangeException.ThrowIfLessThan(planId, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(planVersion, 1);
    ArgumentNullException.ThrowIfNull(stepPositions);

    List<int> positions = [.. stepPositions.Distinct().Order()];
    // An empty chain that immediately reports Completed is a lie about work that did not
    // happen (#313 D10). A chain with no executable step is a plan defect, not a record.
    return positions.Count == 0
        ? throw new ChainInputException("a chain needs at least one executable step position.")
        : new Chain(0, workspaceId.Trim(), planId, planVersion, title.Trim(), ChainStatus.Running,
            null, null, sessionId, now, now, 0, [.. positions.Select(ChainStep.Pending)]);
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
