namespace eThangAgent.ToolDomain;

/// <summary>A tool whose path resolution roots at a workspace. <see cref="RootedAt"/>
///     returns an EQUIVALENT tool — same definition, same collaborators — whose resolver
///     is replaced by a <see cref="WorkspacePathResolver"/> over the requested root,
///     so a child run anchored at a worktree resolves its paths inside that worktree
///     without touching the parent session's registry.</summary>
public interface IWorkspaceScopedTool
{
  /// <summary>Constructs an equivalent tool whose path resolution roots at
  ///     <paramref name="workspaceRoot"/>: a fresh <see cref="WorkspacePathResolver"/>
  ///     over that root, every other dependency the instance already holds reused.</summary>
  ITool RootedAt(string workspaceRoot);
}
