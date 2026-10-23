using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The app applies a host's `context` event to the status bar (issue #255).
///     The accountant lives in the HOST process, so this event is the only way the CTX
///     line advances for a production session - before the fix it stayed empty forever.
///
///     Anti-tautology: the event goes through the PRODUCTION wire payload reader
///     (<see cref="RootTurnContextPayload.Parse"/>) into the PRODUCTION
///     <see cref="StatusViewModel.SetContext"/>, so a shape that drifted between the host
///     and the app fails here rather than in a live session.</summary>
public class ContextEventTests
{
  [Fact]
  public async Task HostContextEvent_UpdatesTheStatusLine()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "context",
        RootTurnContextPayload.Serialize(new ContextSnapshot(
            new ContextStatus(148_200, 148_200, 9_000, 1_000_000, 14.82),
            new ContextBreakdown(12_000, 120_100, 16_000))), 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("CTX 148.2K/1M, 15%", session.ViewModel.Status.ContextDisplay);
    Assert.Contains("System ~12K", session.ViewModel.Status.ContextBreakdownText,
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task HostContextEvent_UnreadablePayload_LeavesTheStatusLineAlone()
  {
    // A status readout is never worth a transcript notice per provider response: an
    // unreadable payload is dropped, and the turn still completes.
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "context", "not json", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("", session.ViewModel.Status.ContextDisplay);
    Assert.Contains("answer", session.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
  }
}
