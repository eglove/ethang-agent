using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>The app applies a host's `usage` event to the status bar's session cost (issue
///     #24). The ledger row is written in the HOST process, so this event is the only way
///     the cost advances for a production session - before it existed the line stayed at
///     its tab-open seed forever.
///
///     Anti-tautology: the event goes through the PRODUCTION wire payload reader
///     (<see cref="RootTurnUsagePayload.Parse"/>) into the PRODUCTION
///     <see cref="StatusViewModel.AddUsage"/>, so a shape that drifted between the host and
///     the app fails here rather than in a live session.</summary>
public class UsageEventTests
{
  private static UsageRow Row() => new("main_loop", "test/model", "openrouter", "ws-1",
      1000, 100, 900, null, 0.000002m, 0.0000002m, 0.000004m);

  [Fact]
  public async Task HostUsageEvent_AdvancesTheSessionCost()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "usage",
        RootTurnUsagePayload.Serialize(Row()), 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("$0.0008", session.ViewModel.Status.CostDisplay);
  }

  [Fact]
  public async Task HostUsageEvent_UnreadablePayload_LeavesTheCostAlone()
  {
    // A status readout is never worth a transcript notice per provider call: an unreadable
    // payload is dropped, and the turn still completes.
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "usage", "not json", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal("", session.ViewModel.Status.CostDisplay);
    Assert.Contains("answer", session.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SeedUsageAsync_ReadsThePersistedRollup()
  {
    // B14: the rows survive a restart, so a resumed session's status bar starts from its
    // real history instead of from zero. The in-memory accountant still starts at zero -
    // that is the deliberate split.
    AgentId rootId = AgentId.NewId();
    AgentSessionViewModel vm = new(
        new RecordingTurnRunner().RunAsync,
        new RootSessionLifecycle(new TestFixtures.StubStore()),
        rootId,
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          UsageQueries = new FixedUsageQueries(new UsageSummary(
              new UsageTotal(2_000_000, 100_000, 1_800_000, 0, 1.25m, 60, 0), [], [])),
        });

    await vm.SeedUsageAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("$1.25", vm.Status.CostDisplay);
    Assert.Contains("60 call(s)", vm.Status.CostBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SeedUsageAsync_NoQueriesWired_LeavesTheCostEmpty()
  {
    // Headless stubs wire no read model: the cost segment stays empty rather than failing.
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);

    await session.ViewModel.SeedUsageAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal("", session.ViewModel.Status.CostDisplay);
  }

  /// <summary>A read model that answers with one fixed summary.</summary>
  private sealed class FixedUsageQueries(UsageSummary summary) : IUsageQueries
  {
    public Task<Result<UsageSummary>> SessionSummaryAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(summary));

    // The status bar never asks for provider performance (issue #295); the member exists so
    // the fake satisfies the read model's full contract rather than half of it.
    public Task<Result<ProviderPerformanceReport>> ProviderPerformanceAsync(
        ProviderPerformanceRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(ProviderPerformanceReport.Empty(UsageScope.Session)));
  }
}
