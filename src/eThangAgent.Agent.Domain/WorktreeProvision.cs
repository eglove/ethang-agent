namespace eThangAgent.AgentDomain;

/// <summary>One worktree provisioned for an isolated child run: its validated name,
///     the worktree directory (inside the provisioning root's .worktrees/ directory),
///     and the branch checked out in it (the worktree branch derived from the name).</summary>
public sealed record WorktreeProvision(string Name, string Path, string Branch);
