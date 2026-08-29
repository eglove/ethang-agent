using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IGitCommitAccess
{
  /// <summary>Stages exactly the given workspace-relative paths. Optional: only
  ///     git_commit with 'files' exercises it.</summary>
  Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default);

  /// <summary>Commits the CURRENT INDEX with the finished message. Never stages.</summary>
  Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default);
}
