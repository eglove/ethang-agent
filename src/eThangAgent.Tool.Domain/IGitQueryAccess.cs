using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Read-only git queries over a repository working tree. Never mutates repository state.</summary>
public interface IGitQueryAccess
{
    Task<Result<GitStatus>> GetStatusAsync(string repoPath, CancellationToken ct = default);
    Task<Result<GitDiff>> GetDiffAsync(string repoPath, string scope, string? path, CancellationToken ct = default);
}
