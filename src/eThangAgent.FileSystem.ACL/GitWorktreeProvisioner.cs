using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

/// <summary>Implements the Agent Domain's worktree-provisioning seam over the git
///     machinery this ACL already owns (GitWorktreeAccess): a provisioned worktree is
///     created under the provisioning root's .worktrees/ directory, on a branch derived
///     from the validated name, with the access's own validation and typed error
///     translation - the domain names intent, this adapter owns the process.</summary>
public sealed class GitWorktreeProvisioner(GitWorktreeAccess worktrees) : IWorktreeProvisioner
{
  private readonly GitWorktreeAccess _worktrees = worktrees ?? throw new ArgumentNullException(nameof(worktrees));

  /// <inheritdoc />
  public async Task<Result<WorktreeProvision>> CreateAsync(string repoRoot, string name, CancellationToken ct = default)
  {
    Result<WorktreeInfo> created = await _worktrees.CreateAsync(repoRoot, name, ct).ConfigureAwait(false);
    return created.IsSuccess
        ? Result.Success(new WorktreeProvision(created.Value.Name, created.Value.Path, created.Value.Branch))
        : Result.Failure<WorktreeProvision>(created.Error);
  }
}
