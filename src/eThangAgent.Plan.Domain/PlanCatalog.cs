namespace eThangAgent.PlanDomain;

/// <summary>One plan row the store could not materialize, named so a listing can render a
///     marker instead of failing. The plan's id is always known (it is read before the
///     corrupt column); <paramref name="Reason"/> carries the store's own diagnosis.
///     Issue #279: a single unreadable row must not blank the whole workspace catalog.</summary>
public sealed record CorruptPlanRow(int PlanId, string Reason);

/// <summary>The result of listing plans: the readable plans plus the rows that could not
///     be read. <see cref="Corrupt"/> is empty for a healthy workspace, so a caller that
///     does not care about corruption reads <see cref="Plans"/> and ignores the rest.</summary>
public sealed record PlanCatalog(IReadOnlyList<Plan> Plans, IReadOnlyList<CorruptPlanRow> Corrupt)
{
  /// <summary>An empty catalog: no plans, nothing corrupt.</summary>
  public static PlanCatalog Empty { get; } = new([], []);
}
