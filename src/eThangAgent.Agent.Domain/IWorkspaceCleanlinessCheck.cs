using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Reads a workspace's git cleanliness for the child-report contract
///     (grand-plan BUG 3): fresh implementers left scratch files behind twice, so a
///     completed run's report must say what the workspace still holds. The seam is
///     domain-owned: the spawner names intent (which directory), the adapter owns
///     git machinery. A directory that is not a git work tree surfaces as a failure
///     Result — the caller stands down, never a crash.</summary>
public interface IWorkspaceCleanlinessCheck
{
  /// <summary>Lists the untracked file paths (workspace-relative) of the git
  ///     repository at <paramref name="workspaceRoot"/>.</summary>
  Task<Result<IReadOnlyList<string>>> UntrackedFilesAsync(string workspaceRoot, CancellationToken ct = default);
}
