namespace eThangAgent.AgentDomain;

/// <summary>One chain row the store could not materialize, named so a listing can render a
///     marker instead of failing. The chain's id is always known (it is read before the
///     columns that can be corrupt). The same containment the plan catalog needed (issue
///     #279): a single unreadable row must not blank every chain in the workspace.</summary>
public sealed record CorruptChainRow(int ChainId, string Reason);

/// <summary>The result of listing chains: the readable chains plus the rows that could not
///     be read. <see cref="Corrupt"/> is empty for a healthy workspace, so a caller that does
///     not care about corruption reads <see cref="Chains"/> and ignores the rest.</summary>
public sealed record ChainCatalog(IReadOnlyList<Chain> Chains, IReadOnlyList<CorruptChainRow> Corrupt)
{
  /// <summary>An empty catalog: no chains, nothing corrupt.</summary>
  public static ChainCatalog Empty { get; } = new([], []);
}
