using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>
/// Application service over <see cref="IPlanStore"/>: builds plans, applies aggregate
/// mutations, stamps <c>UpdatedAt</c>, and CAS-saves with the caller's expectedVersion.
/// Aggregate <see cref="PlanInputException"/>s map to the InvalidTransition DomainError;
/// store failures (PlanNotFound, VersionConflict) pass through verbatim. Events are raised
/// in-process on success and never persisted.
/// </summary>
public sealed class PlanService(IPlanStore store, IPlanTodoCleaner? todoCleaner = null)
{
  private readonly IPlanTodoCleaner? _todoCleaner = todoCleaner;

  // Action<T> events are mandated by the plan's public contract; CA1003's
  // EventHandler<TEventArgs> preference would change the advertised API shape.
#pragma warning disable CA1003
  public event Action<PlanCreated>? Created;
  public event Action<PlanStepChanged>? StepChanged;
  public event Action<PlanStatusChanged>? StatusChanged;
#pragma warning restore CA1003

  public async Task<Result<Plan>> CreateAsync(string title, string goal, string sessionId,
    IReadOnlyList<(string Title, string? Detail, int? TodoId)>? steps, DateTimeOffset now,
    CancellationToken ct = default)
  {
    Plan plan;
    try
    {
      plan = Plan.New(title, goal, sessionId, now);
      foreach ((string stepTitle, string? stepDetail, int? stepTodoId) in steps ?? [])
      {
        plan = plan.AddStep(stepTitle, stepDetail, stepTodoId);
      }
    }
    catch (PlanInputException ex)
    {
      return Result.Failure<Plan>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Plan> created = await store.CreateAsync(plan, ct).ConfigureAwait(false);
    if (created.IsSuccess)
    {
      Created?.Invoke(new PlanCreated(created.Value.Id, created.Value.SessionId, now));
    }

    return created;
  }

  public Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default) => store.GetAsync(id, ct);

  public Task<Result<IReadOnlyList<Plan>>> ListAsync(PlanStatus? status, CancellationToken ct = default) =>
    store.ListAsync(status, ct);

  public async Task<Result<Plan>> AddStepAsync(int id, string title, string? detail, int? todoId,
    int expectedVersion, DateTimeOffset now, CancellationToken ct = default)
  {
    Result<Plan> loaded = await store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return loaded;
    }

    Plan mutated;
    try
    {
      mutated = loaded.Value.AddStep(title, detail, todoId);
    }
    catch (PlanInputException ex)
    {
      return Result.Failure<Plan>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Plan> saved = await store.SaveAsync(mutated with { UpdatedAt = now }, expectedVersion, ct).ConfigureAwait(false);
    if (saved.IsSuccess)
    {
      StepChanged?.Invoke(new PlanStepChanged(id, saved.Value.Steps.Count, "added", now));
    }

    return saved;
  }

  public async Task<Result<Plan>> UpdateStepAsync(int id, int position, Func<PlanStep, PlanStep> mutate,
    int expectedVersion, DateTimeOffset now, CancellationToken ct = default)
  {
    Result<Plan> loaded = await store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return loaded;
    }

    Plan mutated;
    try
    {
      mutated = loaded.Value.UpdateStep(position, mutate);
    }
    catch (PlanInputException ex)
    {
      return Result.Failure<Plan>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Plan> saved = await store.SaveAsync(mutated with { UpdatedAt = now }, expectedVersion, ct).ConfigureAwait(false);
    if (saved.IsSuccess)
    {
      StepChanged?.Invoke(new PlanStepChanged(id, position, "updated", now));
    }

    return saved;
  }

  public async Task<Result<Plan>> RemoveStepAsync(int id, int position, int expectedVersion,
    DateTimeOffset now, CancellationToken ct = default)
  {
    Result<Plan> loaded = await store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return loaded;
    }

    Plan mutated;
    try
    {
      mutated = loaded.Value.RemoveStep(position);
    }
    catch (PlanInputException ex)
    {
      return Result.Failure<Plan>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Plan> saved = await store.SaveAsync(mutated with { UpdatedAt = now }, expectedVersion, ct).ConfigureAwait(false);
    if (saved.IsSuccess)
    {
      StepChanged?.Invoke(new PlanStepChanged(id, position, "removed", now));
    }

    return saved;
  }

  public async Task<Result<PlanSetStatusResult>> SetStatusAsync(int id, PlanStatus target, int expectedVersion,
    DateTimeOffset now, CancellationToken ct = default)
  {
    Result<Plan> loaded = await store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<PlanSetStatusResult>(loaded.Error);
    }

    Plan current = loaded.Value;
    Plan mutated;
    try
    {
      mutated = current.SetStatus(target, now);
    }
    catch (PlanInputException ex)
    {
      return Result.Failure<PlanSetStatusResult>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Plan> saved = await store.SaveAsync(mutated with { UpdatedAt = now }, expectedVersion, ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return Result.Failure<PlanSetStatusResult>(saved.Error);
    }

    StatusChanged?.Invoke(new PlanStatusChanged(id, current.Status, target, now));

    List<int> linked = [.. current.Steps
        .Where(s => s.TodoId is { })
        .Select(s => s.TodoId!.Value)
        .Distinct()];
    List<int> removed = [];
    DomainError? cleanupError = null;
    if (_todoCleaner is not null && linked.Count > 0)
    {
      Result<IReadOnlyList<int>> cleaned =
          await _todoCleaner.RemoveLinkedAsync(linked, ct).ConfigureAwait(false);
      if (cleaned.IsSuccess)
      {
        removed = [.. cleaned.Value];
      }
      else
      {
        cleanupError = cleaned.Error;
      }
    }

    return Result.Success(new PlanSetStatusResult(saved.Value, removed, cleanupError));
  }
}
