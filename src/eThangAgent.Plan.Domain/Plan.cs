using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>
/// Aggregate root for structured plans. Immutable: every mutation returns a copy.
/// Terminal statuses (Completed / Abandoned) freeze the plan; frozen mutation and
/// unknown positions surface as PlanInputException.
/// </summary>
public sealed record Plan(
  int Id,
  string Title,
  string Goal,
  PlanStatus Status,
  string SessionId,
  DateTimeOffset CreatedAt,
  DateTimeOffset UpdatedAt,
  int Version,
  IReadOnlyList<PlanStep> Steps)
{
  /// <summary>Creates a fresh Active plan with unassigned id (0), version 0, and no steps.</summary>
  public static Plan New(string title, string goal, string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(title);
    ArgumentException.ThrowIfNullOrWhiteSpace(goal);
    string t = title.Trim();
    string g = goal.Trim();
    return new Plan(0, t, g, PlanStatus.Active, sessionId, now, now, 0, []);
  }

  /// <summary>Appends a step; positions stay contiguous 1..n.</summary>
  public Plan AddStep(string title, string? detail)
  {
    EnsureMutable();
    PlanStep step = PlanStep.New(Steps.Count + 1, title, detail);
    return this with { Steps = [.. Steps, step], UpdatedAt = DateTimeOffset.UtcNow };
  }

  /// <summary>Inserts a step at the 1-based position, renumbering the tail so positions stay
  ///     contiguous 1..n. A position of <c>Steps.Count + 1</c> appends.</summary>
  public Plan InsertStep(int position, string title, string? detail)
  {
    EnsureMutable();
    ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
    int index = position - 1;
    if (index > Steps.Count)
    {
      throw new PlanInputException(
          $"cannot insert at position {position}; plan #{Id} has {Steps.Count} step(s), so the highest new position is {Steps.Count + 1}");
    }

    List<PlanStep> steps = [.. Steps];
    steps.Insert(index, PlanStep.New(position, title, detail));
    return this with { Steps = Renumbered(steps), UpdatedAt = DateTimeOffset.UtcNow };
  }

  /// <summary>Moves the step at the 1-based <paramref name="from"/> to <paramref name="to"/>, so it
  ///     lands at that position and everything it displaced renumbers to stay contiguous 1..n.
  ///     Both positions must exist; a move to its own position changes nothing.</summary>
  /// <param name="from">The position of the step to move.</param>
  /// <param name="to">The position it should occupy.</param>
  public Plan MoveStep(int from, int to)
  {
    EnsureMutable();
    int index = IndexOf(from);
    if (to < 1 || to > Steps.Count)
    {
      throw new PlanInputException(
          $"cannot move step {from} to position {to}; plan #{Id} has {Steps.Count} step(s), so positions are 1..{Steps.Count}");
    }

    if (from == to)
    {
      return this;
    }

    List<PlanStep> steps = [.. Steps];
    PlanStep moved = steps[index];
    steps.RemoveAt(index);
    steps.Insert(to - 1, moved);
    return this with { Steps = Renumbered(steps), UpdatedAt = DateTimeOffset.UtcNow };
  }

  /// <summary>Applies a mutation to the step at the 1-based position; returns a copy.
  /// The callback receives a copy of the step and returns the mutated value.</summary>
  public Plan UpdateStep(int position, Func<PlanStep, PlanStep> mutate)
  {
    EnsureMutable();
    ArgumentNullException.ThrowIfNull(mutate);
    int index = IndexOf(position);
    PlanStep mutated = mutate(Steps[index] with { });
    List<PlanStep> steps = [.. Steps];
    steps[index] = mutated;
    return this with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow };
  }

  /// <summary>Removes the step at the 1-based position; the tail renumbers to keep 1..n.</summary>
  public Plan RemoveStep(int position)
  {
    EnsureMutable();
    int index = IndexOf(position);
    List<PlanStep> remaining = [.. Steps.Where((_, i) => i != index)];
    return this with
    {
      Steps = [.. remaining.Select((s, i) => s with { Position = i + 1 })],
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  /// <summary>
  /// Transitions the plan's status; only Active -> Completed / Active -> Abandoned is legal.
  /// Reaching a terminal status freezes the plan.
  /// </summary>
  public Plan SetStatus(PlanStatus target, DateTimeOffset now)
  {
    Violation? violation = new PlanStatusTransition(target).ViolationFor(this);
    return violation is not null
      ? throw new PlanInputException(violation.Message)
      : this with { Status = target, UpdatedAt = now };
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

    throw new PlanInputException($"unknown step position {position}; the plan has {Steps.Count} step(s)");
  }

  /// <summary>Renumbers a reordered step list to contiguous 1..n, keeping each step's own fields.</summary>
  private static List<PlanStep> Renumbered(List<PlanStep> steps)
      => [.. steps.Select((step, index) => step with { Position = index + 1 })];

  private void EnsureMutable()
  {
    if (Status == PlanStatus.Active)
    {
      return;
    }

    string status = Status == PlanStatus.Completed ? "completed" : "abandoned";
    throw new PlanInputException($"plan #{Id} is {status}; terminal plans are frozen");
  }
}
