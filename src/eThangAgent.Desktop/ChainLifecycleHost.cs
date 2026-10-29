using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of the chain lifecycle (issue #303 D1): it re-enters a session
///     that already exists, keeping its transcript.
///     <para>WHY THE SHELL AND NOT THE HOST: a session is a TAB CELL - a view-model, a
///     container, and a host process. Resuming one is the same act as typing into its chat
///     box, and the app is the only place a cell exists.</para>
///     <para>A RESUME IS AN ORDINARY TURN, deliberately. The session's conversation is intact
///     and the failure note is just the next thing it reads, so re-entering it needs no
///     special channel - which is what makes D1 true rather than aspirational.</para>
///     <para>EVERY ACT IS BEST EFFORT AND REPORTED, never thrown: the failure fact arrives on
///     an event-pump thread where an escaping exception has no handler.</para></summary>
internal sealed class ChainLifecycleHost(Func<AgentId, string, Task<bool>> resume) : IChainLifecycleHost
{
  /// <inheritdoc />
  public async Task<bool> TryResumeAsync(string sessionId, string message, CancellationToken ct = default)
  {
    _ = ct;
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    ArgumentException.ThrowIfNullOrWhiteSpace(message);
    if (!Guid.TryParse(sessionId, out Guid id))
    {
      return false; // a provisional or malformed id names no session; nothing to resume
    }

    // Named decision (CA1031): the resume runs on an event-pump thread, and a fault here must
    // never reach the publisher. A false answer parks the chain, which is the honest outcome.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      return await resume(new AgentId(id), message).ConfigureAwait(false);
    }
    catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      await Console.Error.WriteLineAsync($"chain lifecycle: resuming session {sessionId} failed: {ex.Message}")
          .ConfigureAwait(false);
      return false;
    }
  }
}
