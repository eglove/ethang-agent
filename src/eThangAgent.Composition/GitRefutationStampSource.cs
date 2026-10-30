using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Implements the Tool Domain's stamp port over the git seam (issue #315 D7).
///     <para>WHY THE HEAD COMMIT AND NOT A TIMESTAMP: code changes; a clock does not track code.
///     "Refuted against abc1234" tells a reader exactly how stale the finding is, and a reader
///     who can see the commit is far behind HEAD knows to re-test - which a TTL can never say.
///     </para>
///     <para>IT NEVER FAILS, and every reason it cannot name a commit is the same answer.
///     <see cref="RefutationRecord.NoCommit"/>: not a git repository, an unborn HEAD (a fresh
///     init has no commit to name), or a git invocation that could not run. Refusing the
///     refutation in those cases would lose a real finding in order to keep one field tidy,
///     and D8 says every session that reaches a refuted verdict writes it.</para>
///     <para>THE ROOT IS RESOLVED PER CALL from <see cref="IWorkspaceContext"/>, not captured at
///     construction: a session's workspace root can be re-pointed (an anchored child, a moved
///     workspace) and a stamp that named the wrong tree's commit would be worse than none.</para>
///     </summary>
internal sealed class GitRefutationStampSource(IGitQueryAccess git, IWorkspaceContext workspace)
    : IRefutationStampSource
{
  private readonly IGitQueryAccess _git = git ?? throw new ArgumentNullException(nameof(git));
  private readonly IWorkspaceContext _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

  public async Task<string> DescribeAsync(CancellationToken ct = default)
  {
    Result<GitStatus> status = await _git.GetStatusAsync(_workspace.WorkspaceRoot, ct).ConfigureAwait(false);
    return status.IsSuccess && status.Value.HeadCommit is { } head
      ? $"commit {head.Hash} ({head.Subject})"
      : RefutationRecord.NoCommit;
  }
}
