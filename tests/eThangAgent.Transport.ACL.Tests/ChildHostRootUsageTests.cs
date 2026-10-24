using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host forwards each recorded provider call as a `usage` wire event (issue
///     #24). The ledger row is written in the HOST process, so an event that did not travel
///     left the app's session cost frozen at its tab-open seed for every production
///     session.
///
///     Anti-tautology: <see cref="ScriptedRootHost"/> runs the REAL <c>RootRunHost</c> over
///     the REAL agent loop, so these tests fail if the host's callback mapping is missing -
///     not merely if a stub was called.</summary>
public class ChildHostRootUsageTests
{
  private sealed class RecordingEventLog : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  private sealed class FixedPriceSource(UsagePrices prices) : IUsagePriceSource
  {
    public Task<UsagePrices> PricesForAsync(string modelId, string? providerName, CancellationToken ct = default)
        => Task.FromResult(prices);
  }

  [Fact]
  public async Task RunTurnAsync_ProviderReportsUsage_EmitsAUsageEvent()
  {
    // The falsifying check for the issue: a host turn whose provider reports usage must put
    // a `usage` event on the wire whose payload parses to the row the loop recorded.
    RecordingEventLog log = new();
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"],
        options: new ScriptedOptions(Usage: new TokenUsage(1_500, 40, 1_200),
            EventLog: log,
            UsagePrices: new FixedPriceSource(new UsagePrices(0.000002m, 0.0000002m, 0.000004m))));
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-usage", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    RootTurnEvent usageEvent = Assert.Single(events, e => e.Kind == "usage");
    RootTurnUsagePayload payload = Assert.IsType<RootTurnUsagePayload>(
        RootTurnUsagePayload.Parse(usageEvent.Payload));
    Assert.Equal(1_500, payload.InputTokens);
    Assert.Equal(40, payload.OutputTokens);
    Assert.Equal(1_200, payload.CachedInputTokens);
    Assert.Equal("scripted/model", payload.ModelId);
    Assert.Equal(0.000002m, payload.PromptPricePerToken);
    Assert.Equal("turn-usage", usageEvent.TurnId);
    // The emitted row is the row that was PERSISTED: the displayed total can never count a
    // call the ledger dropped.
    AgentUsageRecorded recorded = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(recorded.ToUsageRow(), payload.ToRow());
  }

  [Fact]
  public async Task RunTurnAsync_NoUsageReported_EmitsNoUsageEvent()
  {
    // A provider that reports no usage produces no row, so nothing may be emitted: a
    // fabricated spend line would be a lie about what the provider billed.
    RecordingEventLog log = new();
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"],
        options: new ScriptedOptions(EventLog: log,
            UsagePrices: new FixedPriceSource(new UsagePrices(0.000002m, 0.0000002m, 0.000004m))));
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-usage", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.DoesNotContain(events, e => e.Kind == "usage");
  }

  [Fact]
  public async Task RunTurnAsync_NoEventLogWired_EmitsNoUsageEvent()
  {
    // Legacy wiring: no event log means no ledger row, so there is nothing to report and
    // the wire stays byte-identical to before the kind existed.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the answer"],
        options: new ScriptedOptions(Usage: new TokenUsage(1_500, 40)));
    List<RootTurnEvent> events = [];

    _ = await host.RunTurnAsync(new RootTurnRequest("turn-usage", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.DoesNotContain(events, e => e.Kind == "usage");
  }
}
