using eThangAgent.SharedKernel;

namespace eThangAgent.PlanDomain;

/// <summary>Persistence port for plan reviews (issue #216 D5).
///     <para>THE SAME PORT/ADAPTER SPLIT AND ERROR DISCIPLINE AS <see cref="IPlanStore"/>:
///     implementations bind to a workspace at construction, never workspace-parameterized per
///     call, and expected failures flow as <see cref="DomainError"/>s through
///     <see cref="Result{T}"/> - never exceptions. Storage faults surface as
///     <c>StorageUnavailable</c>, an unknown plan as <c>PlanNotFound</c>.</para>
///     <para>ONE PORT, TWO READERS (D7). The chain cursor (#320) and the telemetry report
///     (#309) both read through this interface, so a calibration number and an advance
///     decision are computed from the same rows and cannot disagree.</para>
///     <para>APPEND-ONLY BY CONSTRUCTION: there is no update and no delete. A review is a
///     historical fact about a version of a plan, and revising one would rewrite history
///     rather than record it.</para></summary>
public interface IPlanReviewStore
{
  /// <summary>Persists <paramref name="review"/> and returns it with the store-assigned id.
  ///     <para>REFUSED when the plan does not exist in this workspace (<c>PlanNotFound</c>):
  ///     the foreign key would reject it anyway, and a typed refusal names the real problem
  ///     instead of surfacing a constraint violation as a storage fault.</para></summary>
  Task<Result<PlanReview>> CreateAsync(PlanReview review, CancellationToken ct = default);

  /// <summary>Lists a plan's reviews in CREATION ORDER (issue #216 B2), each carrying the
  ///     version it judged so a caller can tell a current verdict from a stale one.
  ///     <para>An unknown plan id is NOT a failure here: 'this plan has no reviews' and
  ///     'this plan does not exist' are the same answer to the question asked, and a listing
  ///     that failed would make a caller distinguish them for no reason. The empty list is
  ///     the honest answer in both cases.</para></summary>
  Task<Result<IReadOnlyList<PlanReview>>> ListForPlanAsync(int planId, CancellationToken ct = default);

  /// <summary>The newest review of a plan, or null when it has none. The chain cursor's one
  ///     question: 'what did the last reviewer say?'</summary>
  Task<Result<PlanReview?>> LatestForPlanAsync(int planId, CancellationToken ct = default);
}
