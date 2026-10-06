using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>Persistence seam for configured MCP servers and their OAuth tokens
///     (issues #102/#103). Implemented by storage ACLs; the domain never learns SQL.
///     Scope visibility is the reader's contract (B2): a null-workspace row is global and
///     visible to every workspace read; a workspace row is visible only to its own
///     workspace. Name is unique per scope. Removing a server deletes its token rows in
///     the same transaction (B3). Expected failures flow as DomainErrors through Result,
///     never exceptions.</summary>
public interface IMcpServerStore
{
  /// <summary>Every server visible to the reading workspace: the global rows plus the
  ///     workspace's own rows. Ordered by name.</summary>
  Task<Result<IReadOnlyList<McpServerConfig>>> ListAsync(string workspaceId, CancellationToken ct = default);

  /// <summary>Loads one server by id. A row outside the reading workspace's scope
  ///     (another workspace's row) fails McpServerNotFound - ids never leak scopes.</summary>
  Task<Result<McpServerConfig>> GetAsync(int id, string workspaceId, CancellationToken ct = default);

  /// <summary>Inserts a new server config. The store assigns the id and returns the
  ///     stored row. A duplicate name within the same scope fails McpDuplicateName.</summary>
  Task<Result<McpServerConfig>> AddAsync(McpServerConfig server, CancellationToken ct = default);

  /// <summary>Updates a server config's mutable fields (name, transport, command or URL,
  ///     args/env/headers JSON, approval state, pinned version). A duplicate name within
  ///     the target scope fails McpDuplicateName; an unknown id fails McpServerNotFound.</summary>
  Task<Result<McpServerConfig>> UpdateAsync(McpServerConfig server, CancellationToken ct = default);

  /// <summary>Deletes the server row and its token rows in one transaction; reports
  ///     whether a row was removed. An id outside the reading workspace's scope fails
  ///     McpServerNotFound rather than deleting.</summary>
  Task<Result<bool>> DeleteAsync(int id, string workspaceId, CancellationToken ct = default);

  /// <summary>Replaces the stored OAuth tokens for one server and returns the stored
  ///     row (the OAuth increment's carrier; static-header auth stays in the server
  ///     row's headers JSON). An unknown id fails McpServerNotFound.</summary>
  Task<Result<McpOAuthTokens>> SaveTokensAsync(int serverId, McpOAuthTokens tokens, CancellationToken ct = default);

  /// <summary>The stored OAuth tokens for one server, or null when none are stored.</summary>
  Task<Result<McpOAuthTokens?>> GetTokensAsync(int serverId, CancellationToken ct = default);
}
