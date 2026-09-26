using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Read-only git queries over a repository working tree. Never mutates repository state.</summary>
public interface IGitQueryAccess
{
  Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default);
  Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default);

  /// <summary>Resolves the checked-out branch name — "(detached)" on a detached
  ///     HEAD, a failure when the path is not a git repository. The statusline's
  ///     data source; cheap enough to re-run on every HEAD change.</summary>
  Task<Result<string>> GetBranchAsync(string repoPath, CancellationToken ct = default);
}
