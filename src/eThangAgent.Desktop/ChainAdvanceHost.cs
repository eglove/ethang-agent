using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of the mechanical advance (issue #301 D2/D4): it closes the
///     session that completed its step and starts the next one, in the workspace the chain
///     belongs to.
///     <para>WHY THE SHELL AND NOT THE HOST: a chain step's session is a TAB CELL. Closing it
///     and starting its successor is a tab operation - the same in-place replacement `/new`
///     performs - and the app is the only place a tab exists. The host runs the turn; the app
///     owns the surface the turn appears in.</para>
///     <para>THE START IS SEED-ONLY (no prompt): the brief is #302's, and firing a turn with
///     text this issue invents would be a handoff written by a model, which D5 forbids.</para>
///     <para>EVERY ACT IS BEST EFFORT AND REPORTED, never thrown: the completion fact arrives
///     on an event-pump thread where an escaping exception has no handler.</para></summary>
internal sealed class ChainAdvanceHost(
    Func<AgentId, Task> closeSession,
    Func<ChainStepAssignment, string, Task<Result<string>>> startSession) : IChainAdvanceHost
{
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
