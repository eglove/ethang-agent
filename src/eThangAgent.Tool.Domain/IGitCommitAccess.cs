using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IGitCommitAccess
{
  /// <summary>Stages exactly the given workspace-relative paths. Optional: only
  ///     git_commit with 'files' exercises it.</summary>
  Task<Result<bool>> StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default);

  /// <summary>Commits the CURRENT INDEX with the finished message. Never stages.</summary>
  Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default);

  /// <summary>Changed and untracked files with modification times, for the
  ///     verification gate. An inaccessible repository surfaces as a failure:
  ///     the caller decides whether to stand down.</summary>
  Task<Result<IReadOnlyList<(string Path, DateTimeOffset ModifiedUtc)>>> StatusAsync(string repoPath, CancellationToken ct = default);
}
