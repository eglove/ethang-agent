using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Mcp.ACL;

/// <summary>The provider seam (issue #104): one instance per session container hands
///     each workspace its own pooled IMcpServerAccess. One access per workspace =
///     one session pool per workspace (the parent issue's D4); the access owns its
///     sessions' lifetime and disposes them when disposed.</summary>
public sealed class McpServerAccessProvider(
    IMcpServerStore store, IMcpClientSessionPool pool) : IMcpServerAccessProvider
{
  private readonly IMcpServerStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IMcpClientSessionPool _pool = pool ?? throw new ArgumentNullException(nameof(pool));

  /// <inheritdoc />
  public IMcpServerAccess ForWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return new McpServerAccess(_store, _pool, workspaceId);
  }
}
