namespace eThangAgent.ToolDomain;

/// <summary>One git worktree as reported by the access seam: its validated
///     <see cref="WorktreeName"/>-shaped name, the worktree directory path, the branch
///     checked out in it, the short head SHA, whether it is the main worktree, and
///     whether its working tree carries uncommitted changes.</summary>
public sealed record WorktreeInfo(string Name, string Path, string Branch, string HeadShortSha, bool IsMain, bool IsDirty);
