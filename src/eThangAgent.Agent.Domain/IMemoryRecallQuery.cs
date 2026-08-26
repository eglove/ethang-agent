using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Read seam for scoped, branch-aware transcript search with paging. Implemented
///     by the application-layer recall handler; the capability provider renders but never
///     searches.</summary>
public interface IMemoryRecallQuery
{
  /// <param name="query">Null or whitespace browses newest-first; literal input is tokenized,
  ///     never compiled as regex.</param>
  /// <param name="queryMode">Exactly "literal" or "regex".</param>
  /// <param name="scope">Null/"global" or "session:&lt;agentId&gt;" (exact 'D' guid format).</param>
  /// <param name="branches">Exactly "active" or "all".</param>
  /// <param name="role">Null, or one of user/assistant/tool in any casing.</param>
  /// <param name="page">1-based page number.</param>
  /// <param name="pageSize">Page size (max 200).</param>
  /// <param name="ct">Cancellation token for the query.</param>
  Task<Result<RecallPage>> Execute(
      string? query, string queryMode, string? scope, string branches, string? role,
      int page, int pageSize, CancellationToken ct = default);
}
