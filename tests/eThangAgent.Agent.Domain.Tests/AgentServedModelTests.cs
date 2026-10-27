using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

// Pins for RECORDING BOTH MODEL IDS (issue #290): the requested model is what the session
// asked for, the served model is what the provider reported as having answered. For a
// fixed model they are the same string and nothing changes; for a router they diverge and
// only the served id can say what ran and what its prices are. The three cases the issue
// names are pinned here: router (ids differ, cost uses the served table), unreported (null,
// never coerced onto the request), and fixed (identical, cost unchanged).
public class AgentServedModelTests
{
  private const string RequestedModel = "openrouter/free";
  private const string ServedModel = "cohere/north-mini-code:free";

  private static ModelConfig Config =>
      ModelConfig.Create(RequestedModel, "openrouter", 100, 0.5f, 8192).Value!;

  /// <summary>Prices keyed by model id, so a row priced from the wrong table is visible
  ///     as the wrong numbers rather than as an opaque count.</summary>
  private sealed class ModelKeyedPriceSource : IUsagePriceSource
  {
    public List<(string ModelId, string? Provider)> Asked { get; } = [];

    public Task<UsagePrices> PricesForAsync(string modelId, string? providerName, int promptTokens = 0,
        CancellationToken ct = default)
    {
      Asked.Add((modelId, providerName));
      return Task.FromResult(modelId == ServedModel
          ? new UsagePrices(0.0000002m, 0.0000001m, 0.0000008m)
          : new UsagePrices(0.000003m, 0.0000003m, 0.000015m));
    }
  }

  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : Result.Success(new ModelResponse("done", [])));
  }

  private sealed class RecordingEventLog : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent,
        CancellationToken ct = default)
    {
      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  [Fact]
  public async Task SendMessage_RouterServesADifferentModel_RecordsBothIdsAndPricesTheServedOne()
  {
    // THE ROUTER CASE. The session asked for openrouter/free; the provider reported
    // cohere/north-mini-code:free as having answered. Both facts are recorded and the
    // stamped prices are the SERVED model's - pricing from the requested id would bill a
    // model that did not run.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20),
      ServedModelId = ServedModel,
    }));
    RecordingEventLog log = new();
    ModelKeyedPriceSource prices = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = prices, WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(RequestedModel, row.RequestedModelId);
    Assert.Equal(ServedModel, row.ServedModelId);
    Assert.NotEqual(row.RequestedModelId, row.ServedModelId);
    // The pricing basis follows the served id, so the row's ModelId and its prices agree.
    Assert.Equal(ServedModel, row.ModelId);
    Assert.Equal(0.0000002m, row.PromptPricePerToken);
    Assert.Equal(0.0000001m, row.PromptCacheReadPricePerToken);
    Assert.Equal(0.0000008m, row.CompletionPricePerToken);
    Assert.False(row.PricedOnRequestedModel);
    Assert.Equal([(ServedModel, (string?)"openrouter")], prices.Asked);
  }

  [Fact]
  public async Task SendMessage_ProviderReportsNoModel_LeavesServedNullAndRecordsTheFallback()
  {
    // THE UNREPORTED CASE. No model in the response means the provider did not say - so
    // ServedModelId stays null and is NEVER silently set equal to the requested id. The
    // row still prices on the requested model (there is nothing else to price on) and
    // SAYS SO, so the fallback is visible rather than assumed.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20),
    }));
    RecordingEventLog log = new();
    ModelKeyedPriceSource prices = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = prices, WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(RequestedModel, row.RequestedModelId);
    Assert.Null(row.ServedModelId);
    Assert.Equal(RequestedModel, row.ModelId);
    Assert.True(row.PricedOnRequestedModel);
    Assert.Equal([(RequestedModel, (string?)"openrouter")], prices.Asked);
  }

  [Fact]
  public async Task SendMessage_FixedModel_ServedEqualsRequestedAndCostIsUnchanged()
  {
    // THE FIXED-MODEL CASE. Requested and served are the same string, so the row reads
    // exactly as it did before this change: same ModelId, same prices, same cost.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20),
      ServedModelId = RequestedModel,
    }));
    RecordingEventLog log = new();
    ModelKeyedPriceSource prices = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = prices, WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(RequestedModel, row.RequestedModelId);
    Assert.Equal(RequestedModel, row.ServedModelId);
    Assert.Equal(RequestedModel, row.ModelId);
    Assert.False(row.PricedOnRequestedModel);
    Assert.Equal(0.000003m, row.PromptPricePerToken);
    Assert.Equal(0.0000003m, row.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, row.CompletionPricePerToken);
    Assert.Equal([(RequestedModel, (string?)"openrouter")], prices.Asked);
  }

  [Fact]
  public async Task SendMessage_LegacyUnwiredPath_StillRecordsBothIds()
  {
    // The unwired path stays byte-identical in its PRICE members (all null) but the two
    // model ids are facts about the call, not about the wiring, so they are recorded
    // there too - otherwise an unwired session could never say what served it.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20),
      ServedModelId = ServedModel,
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), Config, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(RequestedModel, row.RequestedModelId);
    Assert.Equal(ServedModel, row.ServedModelId);
    // The pricing basis is stamped even unwired, so the per-model rollup buckets this row
    // under the model that served it rather than under the null bucket.
    Assert.Equal(ServedModel, row.ModelId);
    Assert.Null(row.PromptPricePerToken);
    Assert.Null(row.WorkspaceId);
  }

  [Fact]
  public async Task SendMessage_RouterServedCompaction_RecordsBothIdsAndPricesTheServedOne()
  {
    // The compaction row gets the same treatment as a main-loop row: the summarizer was
    // called through the router too, so its served id prices the row and both ids are
    // recorded. Leaving compaction as the one row type that still records requested-only
    // would be the same bug in a smaller room.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("final", [])));
    RecordingEventLog log = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    ModelKeyedPriceSource prices = new();
    Agent agent = new(provider, conversation, Config, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = prices,
          WorkspaceId = "ws-1",
          ContextMonitor = new PressuredMonitor(),
          ContextCompactor = new ServedSummarizingCompactor(new TokenUsage(50, 100),
              RequestedModel, "openrouter", ServedModel),
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>(),
        r => r.EffectiveCallClass.WireName == UsageCallClass.CompactionWire);
    Assert.Equal(RequestedModel, row.RequestedModelId);
    Assert.Equal(ServedModel, row.ServedModelId);
    Assert.Equal(ServedModel, row.ModelId);
    Assert.False(row.PricedOnRequestedModel);
    Assert.Equal(0.0000002m, row.PromptPricePerToken);
    Assert.Contains((ServedModel, (string?)"openrouter"), prices.Asked);
    Assert.DoesNotContain((RequestedModel, (string?)"openrouter"), prices.Asked);
  }

  /// <summary>A monitor parked above the ceiling so the compaction path runs.</summary>
  private sealed class PressuredMonitor : IContextMonitor
  {
    public ContextStatus Status { get; set; } = new(900, 900, 0, 1000, 90.0);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  /// <summary>A compactor reporting the summarizer's requested AND served identity.</summary>
  private sealed class ServedSummarizingCompactor(TokenUsage usage, string requestedModelId,
      string provider, string servedModelId) : IContextCompactor
  {
    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation,
        ModelConfig servingModel, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1,
          usage, SummarizerModelId: requestedModelId, SummarizerProvider: provider,
          SummarizerServedModelId: servedModelId)));

    public Task<Result<CompactionOutcome>> CompactInPlaceAsync(Conversation conversation,
        ModelConfig servingModel, CancellationToken ct = default)
      => CompactAsync(conversation, servingModel, ct);
  }
}
