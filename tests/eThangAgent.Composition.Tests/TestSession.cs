using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>Disposes the sessions a test opened. A session OWNS a host process: its
///     container holds the supervisor, and disposing the container disposes the
///     supervisor, which kills that host. A test that opens a session and never
///     disposes it leaves a STRAY HOST behind — one full suite run leaked 14 of them,
///     and a piped run then hung forever waiting on the inherited stdout (the pipe
///     never reached EOF while the orphaned hosts held it open). The production path
///     is correct: the Desktop disposes a tab's session when the tab closes.</summary>
internal static class TestSession
{
  /// <summary>Disposes one opened session, if it opened at all.</summary>
  public static async ValueTask DisposeAsync(Result<AgentSession> session)
  {
    if (session.IsSuccess)
    {
      await session.Value.Services.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Disposes several opened sessions.</summary>
  public static async ValueTask DisposeAsync(params Result<AgentSession>[] sessions)
  {
    foreach (Result<AgentSession> session in sessions)
    {
      await DisposeAsync(session).ConfigureAwait(false);
    }
  }
}
