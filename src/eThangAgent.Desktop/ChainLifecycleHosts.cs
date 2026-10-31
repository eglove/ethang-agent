using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of ROOT SUPERVISION (issue #301 D5, wired by #323): post the idle
///     nudge into the root session's inbox, and park the chain it is executing.
///     <para>THE NUDGE IS AN INBOX POST, NOT A STEER, and that is a MEASURED constraint rather
///     than a preference: the wire steer (<c>RootSteerCommand</c>) carries a TURN ID and is only
///     accepted while that turn is live, and the whole premise of this watchdog is that the
///     session is IDLE - there is no live turn to steer. The root inbox is the channel that
///     works precisely when nothing is running, which is why it is the one #316 kept.</para>
///     <para>WHY THE APP CAN DO THIS AT ALL: the root inbox is reached through the session's
///     own <see cref="AgentSession.Inbox"/> seam, and the park through its own chain store.
///     Both are objects the shell already holds, so no second lookup can disagree.</para>
///     <para>A HARNESS NUDGE CARRIES NO IDENTITY, deliberately: the identity handshake
///     (#92/#319) exists so the human's PINNED steering echo can be released, and a harness
///     message is never pinned, so it has nothing to release.</para></summary>
/// <param name="shell">Resolves the live shell, lazily.</param>
internal sealed class RootSessionHost(Func<MainViewModel?> shell) : IRootSessionHost
{
  /// <inheritdoc />
  public bool TryNudge(AgentId sessionId, string text)
  {
    if (shell() is not { } live || live.SessionFor(sessionId) is not { } session)
    {
      return false; // closed, or its host is gone: the nudge is spent on nobody
    }

    return session.Inbox.TryPost(text).IsSuccess;
  }

  /// <inheritdoc />
  public async Task ParkAsync(AgentId sessionId, string reason, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    if (shell() is not { } live || live.SessionFor(sessionId) is not { } session)
    {
      return; // the session is gone: its chain is another session's to report
    }

    if (session.Services.GetService<ChainService>() is not { } chains)
    {
      return; // a container with no chain machinery parks nothing
    }

    // The step the session holds is named by the RECORD, never by the caller: the watchdog
    // knows the assignment it was constructed with, and the record is the authority.
    Result<Chain?> found = await chains.FindBySessionAsync(sessionId.ToString(), ct).ConfigureAwait(false);
    if (!found.IsSuccess || found.Value is not { } chain)
    {
      return;
    }

    ChainStep? step = chain.Steps.FirstOrDefault(s =>
        string.Equals(s.SessionId, sessionId.ToString(), StringComparison.Ordinal));
    _ = await chains.ParkAsync(chain.Id, reason, step?.Position, ChainActor.Harness,
        DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
  }
}
