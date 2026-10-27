using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

// Pins for the loop's recording of measured call duration and the serving provider
// (issue #292). The ACL measures; the loop records. Both halves must hold or the ledger
// carries a number nobody measured or drops one somebody did.
public class AgentUsageTimingTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", "openrouter", 100, 0.5f, 8192).Value!;

  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : Result.Success(new ModelResponse("done", [])));
  }

  /// <summary>A price source answering every model with one fixed price set, so the PRICED
  ///     provider (the config's) is stamped and can be compared against the SERVED provider
  ///     (the wire's) — the two members this issue keeps distinct.</summary>
  private sealed class FixedPriceSource : IUsagePriceSource
  {
    private static readonly UsagePrices Prices = new(0.000003m, 0.0000003m, 0.000015m);

    public Task<UsagePrices> PricesForAsync(string modelId, string? providerName, int promptTokens = 0,
        CancellationToken ct = default)
        => Task.FromResult(Prices);
  }

  private sealed class RecordingEventLog : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  [Fact]
  public async Task SendMessage_ResponseCarriesTiming_RowRecordsIt()
  {
    // B1: the row carries the duration measured around the call.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20),
      DurationMs = 1234,
      FirstTokenMs = 321,
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(1234, row.DurationMs);
    Assert.Equal(321, row.FirstTokenMs);
  }

  [Fact]
  public async Task SendMessage_NonStreamingResponse_FirstTokenMsIsNullNotZero()
  {
    // B2's second half: a response that streamed nothing reports NULL for the first token.
    // A zero here would read as instant responsiveness - the opposite of 'never streamed'.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
      DurationMs = 900,
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(900, row.DurationMs);
    Assert.Null(row.FirstTokenMs);
  }

  [Fact]
  public async Task SendMessage_ResponseNamesServingProvider_RowRecordsIt()
  {
    // B3's first half: a provider the wire named is recorded.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
      ServedProvider = "DeepInfra",
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = new FixedPriceSource(), WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal("DeepInfra", row.ServedProvider);
    // The PRICED provider is a different member and stays what the config named: the price
    // table that applied is "openrouter"'s, while the vendor that answered was DeepInfra.
    Assert.Equal("openrouter", row.Provider);
  }

  [Fact]
  public async Task SendMessage_WireNamesNoProvider_ServedProviderIsNullNotThePin()
  {
    // B3's second half, and the one that matters: the config PINS "openrouter", and the
    // wire named nothing. Backfilling the pin would turn a request's intention into a
    // claim about what served - exactly the confident-wrong record this member exists to
    // prevent.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = new FixedPriceSource(), WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.ServedProvider);
    // The pin is still visible where it belongs — as the PRICED provider, not as a claim
    // about who served.
    Assert.Equal("openrouter", row.Provider);
  }

  [Fact]
  public async Task SendMessage_UnwiredSession_StillRecordsTiming()
  {
    // Timing describes the CALL, not the wiring - the same exception the model ids take.
    // An unwired session that lost its durations could never advise on latency.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
      DurationMs = 42,
      FirstTokenMs = 7,
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.CallClass);
    Assert.Equal(42, row.DurationMs);
    Assert.Equal(7, row.FirstTokenMs);
  }

  [Fact]
  public async Task SendMessage_NoTimingMeasured_RowMembersAreNull()
  {
    // Nothing measured is NULL, and null must stay distinguishable from a measured 0.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.DurationMs);
    Assert.Null(row.FirstTokenMs);
  }

  [Fact]
  public async Task SendMessage_MeasuredZeroDuration_IsRecordedAsZero()
  {
    // A call that finished inside a millisecond reports 0 - a real measurement. Coercing
    // it to null would make a fast provider indistinguishable from an unmeasured one.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
      DurationMs = 0,
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(0, row.DurationMs);
    Assert.Null(row.FirstTokenMs);
  }
}
