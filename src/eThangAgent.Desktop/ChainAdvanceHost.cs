using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of the mechanical advance (issue #301 D2/D4): it closes the
///     session that completed its step and starts the next one, in the workspace the chain
///     belongs to.
///     <para>WHY THE SHELL AND NOT THE HOST: a chain step's session is a TAB CELL. Closing it
///     and starting its successor is a tab operation - the same append `/new` performs - and
///     the app is the only place a tab exists. The host runs the turn; the app owns the
///     surface the turn appears in.</para>
///     <para>THE START IS SEED-ONLY (no prompt): the brief is #302's, and firing a turn with
///     text this issue invents would be a handoff written by a model, which D5 forbids.</para>
///     <para>EVERY ACT IS BEST EFFORT AND REPORTED, never thrown: the completion fact arrives
///     on an event-pump thread where an escaping exception has no handler.</para></summary>
internal sealed class ChainAdvanceHost(
    Func<AgentId, Task> closeSession,
    Func<ChainStepAssignment, string, Task<Result<string>>> startSession,
    Func<Chain, string, Task<Result<string>>>? startFinalSession = null,
    Func<string, Task<Result<WorktreeProvision>>>? provisionWorktree = null,
    Func<string, Task<bool>>? worktreeIsDirty = null) : IChainAdvanceHost
{
  /// <inheritdoc />
  public async Task<Result<WorktreeProvision>> ProvisionWorktreeAsync(string name, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    _ = ct;
    if (provisionWorktree is null)
    {
      return Result.Failure<WorktreeProvision>(new DomainError("WorktreeUnsupported",
          "this host cannot provision a worktree; the group member's session was not started."));
    }

    // Named decision (CA1031): the provisioner reports its own typed failures; a thrown fault
    // is converted here so the advance releases the claim instead of leaking it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await provisionWorktree(name).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return Result.Failure<WorktreeProvision>(new DomainError("WorktreeProvisionFailed",
          "the worktree could not be provisioned: " + ex.Message));
    }
  }

  /// <inheritdoc />
  public async Task<bool> WorktreeIsDirtyAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    _ = ct;
    // A host that wires no dirtiness check reports the checkout clean, which is exactly what
    // every step did before a group existed: nothing refuses a completion over a worktree this
    // host cannot read.
    if (worktreeIsDirty is null)
    {
      return false;
    }

    // Named decision (CA1031): A BROKEN CHECK READS AS CLEAN. The refusal exists to nudge a
    // member that forgot to commit, and a git query that threw says nothing about the checkout -
    // refusing a finished step over it would block a completion on an infrastructure fault.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await worktreeIsDirty(path).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      await Console.Error.WriteLineAsync(
          $"chain advance: reading worktree {path} failed: {ex.Message}").ConfigureAwait(false);
      return false;
    }
  }

  /// <inheritdoc />
  public async Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
  {
    _ = ct;
    if (!Guid.TryParse(sessionId, out Guid id))
    {
      return; // a provisional or malformed id names no session; nothing to close
    }

    // Named decision (CA1031): teardown is best effort on every other path in this shell,
    // and the transcript is already persisted - a close fault must not stop the chain.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      await closeSession(new AgentId(id)).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      await Console.Error.WriteLineAsync($"chain advance: closing session {sessionId} failed: {ex.Message}")
          .ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async Task<Result<string>> StartFinalSessionAsync(Chain chain, string predecessorSessionId,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ArgumentException.ThrowIfNullOrWhiteSpace(predecessorSessionId);
    _ = ct;
    if (startFinalSession is null)
    {
      return Result.Failure<string>(new DomainError("FinalLaunchUnsupported",
          "this host cannot start the final agent; the completed chain was not handed back."));
    }

    // Named decision (CA1031): the launcher reports its own typed failures; a thrown fault
    // is converted here so the completion is still recorded.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await startFinalSession(chain, predecessorSessionId).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return Result.Failure<string>(new DomainError("SessionLaunchFailed",
          "the final agent could not be started: " + ex.Message));
    }
  }

  /// <inheritdoc />
  public async Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
      string predecessorSessionId, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(assignment);
    ArgumentException.ThrowIfNullOrWhiteSpace(predecessorSessionId);
    _ = ct;
    // Named decision (CA1031): the launcher reports its own typed failures; a thrown fault
    // is converted here so the advance can release the claim instead of leaking it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await startSession(assignment, predecessorSessionId).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return Result.Failure<string>(new DomainError("SessionLaunchFailed",
          "the next chain step's session could not be started: " + ex.Message));
    }
  }
}
