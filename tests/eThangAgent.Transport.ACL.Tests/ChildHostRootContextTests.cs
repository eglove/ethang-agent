using eThangAgent.ModelDomain;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host forwards the loop's context snapshots as `context` wire events
///     (issue #255). The accountant lives in the HOST process, so a snapshot that did not
///     travel left the app's CTX line frozen for every production session - only the
///     in-process path ever reached Status.SetContext.
///
///     Anti-tautology: <see cref="ScriptedRootHost"/> runs the REAL
///     <c>RootRunHost</c> over the REAL agent loop, so these tests fail if the host's
///     callback mapping is missing - not merely if a stub was called.</summary>
public class ChildHostRootContextTests
{
  [Fact]
  public async Task RunTurnAsync_ProviderReportsUsage_EmitsAContextEvent()
  {
    // The falsifying check for the issue: a host turn whose provider reports usage must
    // put a `context` event on the wire whose payload parses to the accountant's own
    // snapshot. Before the fix, no such event existed at all.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"],
        options: new ScriptedOptions(Usage: new TokenUsage(1_500, 40)));
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-ctx", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    RootTurnEvent contextEvent = Assert.Single(events, e => e.Kind == "context");
    RootTurnContextPayload payload = Assert.IsType<RootTurnContextPayload>(
        RootTurnContextPayload.Parse(contextEvent.Payload));
    Assert.Equal(1_500, payload.LastInputTokens);
    Assert.Equal(1_500, payload.TotalInputTokens);
    Assert.Equal(40, payload.TotalOutputTokens);
    // The scripted host's window is 200K, so utilization is the provider-scored input
    // against it - the same arithmetic the in-process status bar read.
    Assert.Equal(200_000, payload.ContextWindow);
    Assert.Equal(0.75, payload.UtilizationPercent);
    Assert.Equal("turn-ctx", contextEvent.TurnId);
  }

  [Fact]
  public async Task RunTurnAsync_TheContextEventCarriesTheBreakdown()
  {
    // The CTX tooltip breakdown must arrive with the snapshot (acceptance criterion):
    // a payload that carried only the totals would leave the hover line empty.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"],
        options: new ScriptedOptions(Usage: new TokenUsage(1_000, 10)));
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-ctx", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    RootTurnContextPayload payload = Assert.IsType<RootTurnContextPayload>(
        RootTurnContextPayload.Parse(Assert.Single(events, e => e.Kind == "context").Payload));
    RootTurnContextBreakdownPayload breakdown = Assert.IsType<RootTurnContextBreakdownPayload>(
        payload.Breakdown);
    // The three buckets are shares of the provider-scored input: they must account for
    // the whole report, never a partial one.
    int sum = (breakdown.SystemPromptTokens ?? 0) + (breakdown.MessageTokens ?? 0)
        + (breakdown.ToolTokens ?? 0);
    Assert.Equal(1_000, sum);
  }

  [Fact]
  public async Task RunTurnAsync_NoUsageReported_EmitsNoContextEvent()
  {
    // A provider that reports no usage produces no accounting: the host must not invent a
    // snapshot, because a fabricated CTX line would be a lie about what the provider
    // scored.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"]);
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-ctx", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.DoesNotContain(events, e => e.Kind == "context");
  }
}
