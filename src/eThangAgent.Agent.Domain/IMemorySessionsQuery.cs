using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Read seam for listing persisted sessions newest-first with lineage, size,
///     lifecycle, and the constant hot tier. Implemented by the application-layer
///     sessions handler; the capability provider renders but never lists.</summary>
public interface IMemorySessionsQuery
{
    /// <param name="scope">Null/"global" or "session:&lt;agentId&gt;" — validated, not applied:
    ///     the listing always spans all persisted rows per the approved task contract.</param>
    /// <param name="branches">Exactly "active" or "all".</param>
    /// <param name="limit">1..500.</param>
    Task<Result<IReadOnlyList<SessionSummary>>> Execute(
        string? scope, string branches, int limit, CancellationToken ct = default);
}
