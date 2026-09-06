using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>
/// Persistence port for plans. Implementations bind to a workspace at construction -
/// never workspace-parameterized per call. Expected failures flow as <see cref="DomainError"/>s
/// (codes PlanNotFound, VersionConflict) through <see cref="Result{T}"/> - never exceptions.
/// </summary>
public interface IPlanStore
{
  /// <summary>Persists a new plan; the store assigns the Id and returns the plan at Version 1.</summary>
  Task<Result<Plan>> CreateAsync(Plan draft, CancellationToken ct = default);

  /// <summary>Loads a plan by id; fails with PlanNotFound.</summary>
  Task<Result<Plan>> GetAsync(int id, CancellationToken ct = default);

  /// <summary>Lists plans, optionally filtered by status.</summary>
  Task<Result<IReadOnlyList<Plan>>> ListAsync(PlanStatus? status, CancellationToken ct = default);

  /// <summary>Compare-and-swap save: persists <paramref name="plan"/> only while the stored
  /// version still equals <paramref name="expectedVersion"/>; the store bumps Version on success.
  /// Fails with PlanNotFound or VersionConflict.</summary>
  Task<Result<Plan>> SaveAsync(Plan plan, int expectedVersion, CancellationToken ct = default);
}
