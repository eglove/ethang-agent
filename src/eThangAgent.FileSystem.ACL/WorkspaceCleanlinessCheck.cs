using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>Implements the Agent Domain's workspace-cleanliness seam over the git
///     status query this ACL already owns (DirectGitAccess): the domain names intent
///     (which directory), this adapter owns the git machinery and translates a
///     non-repository directory into the typed failure the spawner stands down on.
///     Only untracked paths matter for the child-report contract.</summary>
public sealed class WorkspaceCleanlinessCheck(IGitQueryAccess git) : IWorkspaceCleanlinessCheck
{
  private readonly IGitQueryAccess _git = git ?? throw new ArgumentNullException(nameof(git));

  /// <inheritdoc />
  public async Task<Result<IReadOnlyList<string>>> UntrackedFilesAsync(string workspaceRoot, CancellationToken ct = default)
  {
    Result<GitStatus> status = await _git.GetStatusAsync(workspaceRoot, ct).ConfigureAwait(false);
    return status.IsSuccess
        ? Result.Success<IReadOnlyList<string>>(status.Value.Untracked)
        : Result.Failure<IReadOnlyList<string>>(status.Error);
  }
}
