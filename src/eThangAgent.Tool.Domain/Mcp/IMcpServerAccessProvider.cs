namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The per-workspace access factory (issue #104): the composition resolves
///     one provider per session container; each workspace's access owns its pooled
///     sessions. The tool resolves its access through this seam so a child anchored
///     at another workspace gets that workspace's pool.</summary>
public interface IMcpServerAccessProvider
{
  /// <summary>The workspace-scoped access: lazy connect, cached discovery, and the
  ///     approval gate are per workspace. The caller owns the returned access's
  ///     lifetime.</summary>
  IMcpServerAccess ForWorkspace(string workspaceId);
}
