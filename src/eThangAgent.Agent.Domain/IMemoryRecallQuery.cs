using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>One recall query: what to search, where, and how to page. Members carry the
///     former loose parameters verbatim — validation lives with the handler, which
///     rejects unknown values with typed errors naming the valid spellings.</summary>
/// <param name="Query">Null or whitespace browses newest-first; literal input is tokenized,
///     never compiled as regex.</param>
/// <param name="QueryMode">Exactly "literal" or "regex".</param>
/// <param name="Scope">Null/"global" or "session:&lt;agentId&gt;" (exact 'D' guid format).</param>
/// <param name="Branches">Exactly "active" or "all".</param>
/// <param name="Role">Null, or one of user/assistant/tool in any casing.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Page size (max 200).</param>
public sealed record RecallRequest(
    string? Query,
    string QueryMode,
    string? Scope,
    string Branches,
    string? Role,
    int Page,
    int PageSize);

/// <summary>Read seam for scoped, branch-aware transcript search with paging. Implemented
///     by the application-layer recall handler; the capability provider renders but never
///     searches.</summary>
public interface IMemoryRecallQuery
{
  /// <param name="request">The query to run; every member is validated strictly by the
  ///     implementation.</param>
  /// <param name="ct">Cancellation token for the query.</param>
  Task<Result<RecallPage>> Execute(RecallRequest request, CancellationToken ct = default);
}
