using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>Builds the advance service for one session (issue #301 D1/D2).
///     <para>WHY A FACTORY AND NOT A REGISTRATION: the chain store is bound to a WORKSPACE at
///     construction and lives in that session's own container, so there is no single instance
///     to inject. The session is the key, and this is the one place it is turned into an
///     advance.</para>
///     <para>Null is a first-class answer: a container that wired no chain machinery (a
///     headless or test host) advances nothing, and the shell then behaves exactly as it did
///     before this issue.</para></summary>
internal static class ChainAdvanceFactory
{
  /// <summary>The advance service for <paramref name="session"/>, or null when its container
  ///     wired no chain machinery.</summary>
  /// <param name="session">The session whose container owns the chain store.</param>
  /// <param name="shell">Resolves the live shell, lazily - the shell constructs the sessions
  ///     it will later be asked to close and continue.</param>
  public static ChainAdvanceService? For(AgentSession session, Func<MainViewModel?> shell)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(shell);
    return session.Services.GetService<ChainService>() is { } chains
      ? new ChainAdvanceService(chains, new ChainAdvanceHost(
          closeSession: rootId => shell() is { } live
              ? live.CloseSessionAsync(rootId)
              : Task.CompletedTask,
          startSession: (assignment, predecessor) => shell() is { } live
              ? live.StartChainStepSessionAsync(assignment, predecessor)
              : Task.FromResult(Result.Failure<string>(new DomainError("TabClosed",
                  "the shell is not ready; the next chain step was not started."))),
          startFinalSession: (chain, predecessor) => shell() is { } live
              ? live.StartFinalSessionAsync(chain, predecessor)
              : Task.FromResult(Result.Failure<string>(new DomainError("TabClosed",
                  "the shell is not ready; the final agent was not started."))),
          // A GROUP MEMBER'S CHECKOUT: the provisioner is the session container's own, so the
          // worktree is made in the workspace that container is bound to - the same directory
          // the member's session would otherwise have run in.
          provisionWorktree: name => ProvisionWorktreeAsync(session, name),
          // A MEMBER'S UNCOMMITTED WORKTREE: the same read-only git seam the status tool uses,
          // asked about the member's own checkout rather than the workspace.
          worktreeIsDirty: path => WorktreeIsDirtyAsync(session, path),
          // A LANDED GROUP'S CHECKOUTS: removed by the same seam that made them, once the
          // reconciler has merged their branches into the workspace checkout.
          removeWorktree: path => RemoveWorktreeAsync(session, path)),
          // The separation rule and the implementer's recorded model (issue #218): the gate parks
          // a review step whose reviewer matches the implementer, and the agent store is where the
          // durable record of what actually served lives.
          reviewers: session.Services.GetService<ChainReviewerGate>(),
          agents: session.Services.GetService<IAgentStore>(),
          // THE REVIEW CURSOR'S ONE READ (issue #320): the verdict a review step's session
          // recorded. Without it a review step would advance exactly as it did before, which
          // is the silent discard this issue closes.
          reviews: session.Services.GetService<IChainReviewSource>())
      : null;
  }

  /// <summary>Provisions one worktree in <paramref name="session"/>'s workspace and reports its
  ///     directory and branch. A container that wired no provisioner refuses by name rather than
  ///     launching a group member in the shared checkout: two members writing one directory is
  ///     the interference the worktrees exist to prevent.</summary>
  /// <param name="session">The session whose container owns the provisioner.</param>
  /// <param name="name">The worktree name, already in the shape the worktree rule accepts.</param>
  private static async Task<Result<WorktreeProvision>> ProvisionWorktreeAsync(AgentSession session,
      string name)
    => session.Services.GetService<IWorktreeProvisioner>() is { } provisioner
      ? await provisioner.CreateAsync(session.WorkspaceRoot, name).ConfigureAwait(false)
      : Result.Failure<WorktreeProvision>(new DomainError("WorktreeUnsupported",
          "this workspace cannot provision a worktree; the group member's session was not started."));

  /// <summary>True when the checkout at <paramref name="path"/> holds changes nobody committed,
  ///     read through the container's own git query.
  ///     <para>AN UNREADABLE CHECKOUT READS AS CLEAN, and so does a container that wired no git
  ///     access: the refusal exists to nudge a member that forgot to commit, and a git query that
  ///     failed says nothing about the checkout. Refusing a finished step over it would block a
  ///     completion on an infrastructure fault.</para></summary>
  /// <param name="session">The session whose container owns the git query.</param>
  /// <param name="path">The worktree directory.</param>
  private static async Task<bool> WorktreeIsDirtyAsync(AgentSession session, string path)
  {
    if (session.Services.GetService<IGitQueryAccess>() is not { } git)
    {
      return false;
    }

    Result<GitStatus> status = await git.GetStatusAsync(path).ConfigureAwait(false);
    return status.IsSuccess
      && (status.Value.Staged.Count > 0 || status.Value.Unstaged.Count > 0
          || status.Value.Untracked.Count > 0);
  }

  /// <summary>Removes one worktree of <paramref name="session"/>'s workspace. A container that
  ///     wired no worktree access removes nothing: the checkout stays, which is what every step
  ///     did before a group existed.</summary>
  /// <param name="session">The session whose container owns the worktree access.</param>
  /// <param name="path">The worktree directory.</param>
  private static async Task RemoveWorktreeAsync(AgentSession session, string path)
  {
    if (session.Services.GetService<IGitWorktreeAccess>() is not { } worktrees)
    {
      return;
    }

    // The worktree is named by its directory, and the access addresses one by name: the name is
    // the directory's last segment, which is the name the provisioner was given.
    Result<bool> removed = await worktrees
        .RemoveAsync(session.WorkspaceRoot, Path.GetFileName(path), force: false)
        .ConfigureAwait(false);
    if (!removed.IsSuccess)
    {
      await Console.Error.WriteLineAsync(
          $"chain advance: removing worktree {path} failed: {removed.Error.Message}").ConfigureAwait(false);
    }
  }

  /// <summary>The floor service for <paramref name="session"/> (issue #304): park the chain when
  ///     the turn attempted an act on the universal floor.
  ///     <para>The same null-is-an-answer contract as <see cref="For"/>: a container that wired
  ///     no floor refuses nothing, and the shell behaves exactly as it did before.</para></summary>
  /// <param name="session">The session whose container owns the chain store and the ledger.</param>
  public static ChainFloorService? FloorFor(AgentSession session)
  {
    ArgumentNullException.ThrowIfNull(session);
    return session.Services.GetService<ChainService>() is { } chains
        && session.Services.GetService<ChainFloorLedger>() is { } floor
      ? new ChainFloorService(chains, floor)
      : null;
  }

  /// <summary>The lifecycle service for <paramref name="session"/> (issue #303): resume a failed
  ///     step's session, or park the chain when the budget is spent.
  ///     <para>The same null-is-an-answer contract as <see cref="For"/>: a container that wired no
  ///     chain machinery retries nothing, and the shell behaves exactly as it did before.</para></summary>
  /// <param name="session">The session whose container owns the chain store.</param>
  /// <param name="shell">Resolves the live shell, lazily - the shell owns the cells a resume
  ///     re-enters.</param>
  public static ChainLifecycleService? LifecycleFor(AgentSession session, Func<MainViewModel?> shell)
  {
    ArgumentNullException.ThrowIfNull(session);
    ArgumentNullException.ThrowIfNull(shell);
    return session.Services.GetService<ChainService>() is { } chains
      ? new ChainLifecycleService(chains, new ChainLifecycleHost((rootId, message) => Task.FromResult(
          shell() is { } live && live.ResumeChainStepSessionAsync(rootId, message))))
      : null;
  }
}
