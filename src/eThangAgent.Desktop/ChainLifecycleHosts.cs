using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop;

/// <summary>The shell's half of ROOT SUPERVISION (issue #301 D5, wired by #323): post the idle
///     nudge an idle session with a turn, and park the chain it is executing.
///     <para>THE NUDGE STARTS A TURN (stakeholder decision, 2026-10-31), and that is what makes
///     the ladder's first rung real. Posting to the inbox was tried first and is INERT here: the
///     loop drains at turn entry, at iteration boundaries, and at a mid-stream safe point - none
///     of which an IDLE session reaches - and the wire steer needs a live turn id, which by
///     definition does not exist. A queued nudge would be read by the session's next turn or
///     never. The premise of this watchdog is a session that has STOPPED, so the only act that
///     can reach it is one that starts a turn.</para>
///     <para>WHY THE HARNESS MAY START ONE HERE: the same authority the kickoff already uses
///     (#323) - a session launched FOR a chain step is doing harness-directed work, and a nudge
///     is the harness telling it to continue work it was already assigned. It is not the harness
///     inventing a task; it is re-entering a session that owes a completion.</para>
///     <para>A NUDGE THAT CANNOT LAND SPENDS NO ATTEMPT: this answers false when the session is
///     gone, so a closed tab never burns the wrap-up budget.</para>
///     <para>WHY THE APP CAN DO THIS AT ALL: the cell is a tab operation, and the park goes
///     through the session's own chain store. Both are objects the shell already holds, so no
///     second lookup can disagree with the one the nudge just used.</para>
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

    // START A TURN: the act that reaches an idle session, for the reason in the summary. A
    // busy cell answers false, which is the one case where queuing is right - the session is
    // working, so the nudge waits behind its turn instead of racing it.
    if (live.NudgeChainStepSession(sessionId, text))
    {
      return true;
    }

    // FALLBACK FOR A BUSY SESSION: queue it rather than dropping it. The loop drains it at its
    // next safe point, which is exactly when a working session should read it.
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
