using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IGitCommitAccess
{
  /// <summary>Commits the CURRENT INDEX with the finished message. Never stages.</summary>
  Task<Result<GitCommitOutcome>> CommitAsync(string repoPath, string message, CancellationToken ct = default);
}
