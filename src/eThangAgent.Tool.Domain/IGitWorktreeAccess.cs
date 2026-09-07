using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Git worktree operations over a repository. Names and paths are plain
///     strings at this seam; every name accepted here must be validated through
///     <see cref="WorktreeName"/> before use, and implementations translate the
///     underlying git machinery's outcomes into typed <c>Result</c> failures —
///     never exceptions for expected failures.</summary>
public interface IGitWorktreeAccess
{
  /// <summary>Lists every worktree of the repository at <paramref name="repoRoot"/>.</summary>
  Task<Result<IReadOnlyList<WorktreeInfo>>> ListAsync(string repoRoot, CancellationToken ct = default);

  /// <summary>Creates a worktree named <paramref name="name"/> in the repository at
  ///     <paramref name="repoRoot"/> and reports the new worktree.</summary>
  Task<Result<WorktreeInfo>> CreateAsync(string repoRoot, string name, CancellationToken ct = default);

  /// <summary>Removes the worktree named <paramref name="name"/> from the repository at
  ///     <paramref name="repoRoot"/>; reports whether it was removed.</summary>
  Task<Result<bool>> RemoveAsync(string repoRoot, string name, bool force, CancellationToken ct = default);
}
