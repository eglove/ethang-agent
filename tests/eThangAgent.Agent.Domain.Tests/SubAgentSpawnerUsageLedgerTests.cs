using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Child usage attribution (issue #23, B5): a child's provider call records a row
///     under the CHILD's own persisted id, carrying the session's prices and workspace —
///     never the parent's id. The session a child's spend belongs to is derived by walking
///     agents.parent_id at read time (#24), so nothing is stamped here.</summary>
public class SubAgentSpawnerUsageLedgerTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static readonly UsagePrices Known = new(0.000003m, 0.0000003m, 0.000015m);

  private sealed class FixedPriceSource(UsagePrices prices) : IUsagePriceSource
  {
    public Task<UsagePrices> PricesForAsync(string modelId, string? providerName, int promptTokens = 0,
        CancellationToken ct = default)
        => Task.FromResult(prices);
  }

  /// <summary>Records every event a child run appends.</summary>
  private sealed class RecordingEventLog : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  private static AgentRecord Child() => AgentRecord.Spawned(
      AgentId.NewId(), null, 0, "default/sub-model", null, "do things", FixedNow);

  [Fact]
  public async Task RunAsync_ChildUsageRow_CarriesTheChildsOwnIdAndTheSessionsPrices()
  {
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])
    {
      Usage = new TokenUsage(100, 20, 64, null, 7),
    }));
    RecordingEventLog log = new();
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store, new ToolRegistry([]),
        new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "default/sub-model"),
        EventLog: log, UsagePrices: new FixedPriceSource(Known), WorkspaceId: "ws-1"));

    AgentRecord child = Child();
    _ = await spawner.RunAsync(child, CancellationToken.None);

    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    // B5: the row belongs to the CHILD, never the parent.
    Assert.Equal(child.Id, row.AgentId);
    Assert.Same(UsageCallClass.MainLoop, row.EffectiveCallClass);
    Assert.Equal("ws-1", row.WorkspaceId);
    Assert.Equal(0.000003m, row.PromptPricePerToken);
    Assert.Equal(0.0000003m, row.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, row.CompletionPricePerToken);
  }

  [Fact]
  public async Task RunAsync_UnwiredChild_RecordsTheLegacyRowShape()
  {
    // B9 for children: no price source and no workspace means the pre-#23 row shape.
    FakeAgentStore store = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("report", [])
    {
      Usage = new TokenUsage(100, 20),
    }));
    RecordingEventLog log = new();
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), store, new ToolRegistry([]),
        new StaticPromptProvider("guide text"), new SubAgentOptions(DefaultModel: "default/sub-model"),
        EventLog: log));

    _ = await spawner.RunAsync(Child(), CancellationToken.None);

    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.CallClass);
    Assert.Null(row.WorkspaceId);
    Assert.Null(row.PromptPricePerToken);
  }
}
