using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

// Pins for the usage LEDGER's recording sites (issue #23): the loop's safe point records
// one priced row per provider call, the compactor's summarizer call is recorded with the
// SUMMARIZER's identity (a handoff included), and every unwired or failing path stays
// exactly as forgiving as it was. Behaviors B1, B2, B3, B5, B6, B7, B8, B9, B10.
public class AgentUsageLedgerTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192, pricingProvider: "openrouter").Value!;

  private static readonly UsagePrices Known = new(0.000003m, 0.0000003m, 0.000015m);

  /// <summary>A price source that answers every model with one fixed price set and
  ///     records what it was asked for.</summary>
  private sealed class FixedPriceSource(UsagePrices prices) : IUsagePriceSource
  {
    public List<string> Asked { get; } = [];

    public Task<UsagePrices> PricesForAsync(string modelId, int promptTokens = 0,
        CancellationToken ct = default)
    {
      Asked.Add(modelId);
      return Task.FromResult(prices);
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

  private sealed class RecordingEventLog(bool fault = false) : IAgentEventLog
  {
    public List<AgentDomainEvent> Events { get; } = [];

    public Task<Result<string>> AppendEventAsync(AgentDomainEvent domainEvent, CancellationToken ct = default)
    {
      if (fault)
      {
        throw new InvalidOperationException("store exploded");
      }

      Events.Add(domainEvent);
      return Task.FromResult(Result.Success(domainEvent.AgentId.ToString()));
    }
  }

  /// <summary>A compactor reporting the summarizer's usage and identity, as the real one
  ///     does after issue #23.</summary>
  private sealed class SummarizingCompactor(TokenUsage? usage, string? modelId, string? provider,
      bool handoff = false) : IContextCompactor
  {
    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel,
        CancellationToken ct = default)
      => Task.FromResult(Result.Success(handoff
          ? CompactionOutcome.Handoff("the summary", usage, modelId, provider)
          : new CompactionOutcome(1, conversation.Messages.Count - 1, usage,
              SummarizerModelId: modelId, SummarizerProvider: provider)));

    public Task<Result<CompactionOutcome>> CompactInPlaceAsync(Conversation conversation, ModelConfig servingModel,
        CancellationToken ct = default)
      => Task.FromResult(Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1, usage,
          SummarizerModelId: modelId, SummarizerProvider: provider)));
  }

  /// <summary>A monitor parked above the ceiling so the compaction path runs.</summary>
  private sealed class PressuredMonitor(int window = 1000) : IContextMonitor
  {
    public ContextStatus Status { get; set; } = new(900, 900, 0, window, 90.0);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  [Fact]
  public async Task SendMessage_WithPricesWired_StampsClassProviderWorkspaceAndPrices()
  {
    // B1 + B6: the row carries the class, the serving provider, the workspace, and all
    // three prices resolved at write time.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20, 64, null, 7),
    }));
    RecordingEventLog log = new();
    FixedPriceSource prices = new(Known);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = prices, WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Same(UsageCallClass.MainLoop, row.CallClass);
    Assert.Equal("openrouter", row.PricingProvider);
    Assert.Equal("ws-1", row.WorkspaceId);
    Assert.Equal(0.000003m, row.PromptPricePerToken);
    Assert.Equal(0.0000003m, row.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, row.CompletionPricePerToken);
    Assert.Equal(["test-model"], prices.Asked);
  }

  [Fact]
  public async Task SendMessage_StreamingPath_RecordsTheSameRowShape()
  {
    // B2: streaming and non-streaming reach the same safe point, so the row is identical
    // in shape. The loop always calls SendStreamingAsync, whose default delegates to
    // SendAsync - this pins that the recording site is not bypassed either way.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = new FixedPriceSource(Known), WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Same(UsageCallClass.MainLoop, row.CallClass);
    Assert.Equal(10, row.InputTokens);
  }

  [Fact]
  public async Task SendMessage_ModelWithoutCachePrice_LeavesThatMemberNull()
  {
    // B6: 'no cache price advertised' is a different fact from 'cache is free'.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = new FixedPriceSource(new UsagePrices(0.000003m, null, 0.000015m)),
          WorkspaceId = "ws-1",
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Equal(0.000003m, row.PromptPricePerToken);
    Assert.Null(row.PromptCacheReadPricePerToken);
    Assert.Equal(0.000015m, row.CompletionPricePerToken);
  }

  [Fact]
  public async Task SendMessage_ModelAbsentFromCatalog_StampsAllThreePricesNull()
  {
    // B6: a catalog miss contributes tokens but no cost - never a fabricated price.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(10, 2),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = new FixedPriceSource(UsagePrices.Unknown),
          WorkspaceId = "ws-1",
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.PromptPricePerToken);
    Assert.Null(row.PromptCacheReadPricePerToken);
    Assert.Null(row.CompletionPricePerToken);
    Assert.Equal(10, row.InputTokens); // tokens still recorded
  }

  [Fact]
  public async Task SendMessage_Unwired_RecordsTheLegacyRowShape()
  {
    // B9: no price source and no workspace id means byte-identical legacy behavior -
    // every member this issue added stays null.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])
    {
      Usage = new TokenUsage(100, 20, 64, null, 7),
    }));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>());
    Assert.Null(row.CallClass);
    Assert.Null(row.WorkspaceId);
    Assert.Null(row.PricingProvider);
    Assert.Null(row.PromptPricePerToken);
    Assert.Null(row.PromptCacheReadPricePerToken);
    Assert.Null(row.CompletionPricePerToken);
    Assert.Same(UsageCallClass.MainLoop, row.EffectiveCallClass);
  }

  [Fact]
  public async Task SendMessage_NoUsageReport_RecordsNoRow()
  {
    // B7: an absent report is a different fact from a zero one.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("done", [])));
    RecordingEventLog log = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { EventLog = log, UsagePrices = new FixedPriceSource(Known), WorkspaceId = "ws-1" });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Empty(log.Events.OfType<AgentUsageRecorded>());
  }

  [Fact]
  public async Task SendMessage_StoreFault_CountsTheDropAndSurfacesItOnce()
  {
    // B8: a recording failure never fails the turn, and the drop is COUNTED and
    // surfaced once per turn rather than swallowed silently.
    ScriptedProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])
        {
          Usage = new TokenUsage(100, 20),
        }),
        Result.Success(new ModelResponse("done", [])
        {
          Usage = new TokenUsage(150, 30),
        }));
    RecordingEventLog log = new(fault: true);
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "content")]),
        new AgentOptions { EventLog = log, UsagePrices = new FixedPriceSource(Known), WorkspaceId = "ws-1" });
    List<string> notices = [];

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: notices.Add), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
    // Two calls dropped, one notice: the drop is visible, not spammed per call.
    string notice = Assert.Single(notices);
    Assert.Contains("[usage ledger]", notice, StringComparison.Ordinal);
    Assert.Contains("store exploded", notice, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Compaction_RecordsTheSummarizerIdentityAsItsOwnRow()
  {
    // B3: the compaction row names the SUMMARIZER's model and provider, not the serving
    // model - the price stamped must be the price of the model that actually ran.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("final", [])));
    RecordingEventLog log = new();
    FixedPriceSource prices = new(Known);
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = prices,
          WorkspaceId = "ws-1",
          ContextMonitor = new PressuredMonitor(),
          ContextCompactor = new SummarizingCompactor(new TokenUsage(50, 100), "summarizer", "openrouter"),
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>(),
        row => row.EffectiveCallClass.WireName == UsageCallClass.CompactionWire);
    Assert.Equal(50, row.InputTokens);
    Assert.Equal(100, row.OutputTokens);
    Assert.Equal("openrouter", row.PricingProvider);
    Assert.Equal("ws-1", row.WorkspaceId);
    // Priced against the SUMMARIZER's model, not the serving one.
    Assert.Contains("summarizer", prices.Asked);
  }

  [Fact]
  public async Task Compaction_HandoffOutcome_StillRecordsTheSummarizerCall()
  {
    // B3: the summarizer was called and billed whether or not the conversation was
    // compacted, so a HANDOFF outcome records its row too.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("final", [])));
    RecordingEventLog log = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = new FixedPriceSource(Known),
          WorkspaceId = "ws-1",
          ContextMonitor = new PressuredMonitor(),
          ContextCompactor = new SummarizingCompactor(new TokenUsage(50, 100), "summarizer", "openrouter",
              handoff: true),
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>(),
        row => row.EffectiveCallClass.WireName == UsageCallClass.CompactionWire);
    Assert.Equal(50, row.InputTokens);
  }

  [Fact]
  public async Task Compaction_WithoutSummarizerIdentity_RecordsNoCompactionRow()
  {
    // The tail-compression fallback makes NO summarizer call: an outcome with no
    // summarizer model id has no call to attribute, so nothing is recorded.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("final", [])));
    RecordingEventLog log = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          UsagePrices = new FixedPriceSource(Known),
          WorkspaceId = "ws-1",
          ContextMonitor = new PressuredMonitor(),
          ContextCompactor = new SummarizingCompactor(new TokenUsage(50, 100), modelId: null, provider: null),
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.DoesNotContain(log.Events.OfType<AgentUsageRecorded>(),
        row => row.EffectiveCallClass.WireName == UsageCallClass.CompactionWire);
  }

  [Fact]
  public async Task Compaction_Unwired_RecordsTheCompactionRowWithNullPrices()
  {
    // B9 for the compaction path: no price source means null prices, and the row still
    // records the summarizer's tokens and identity.
    ScriptedProvider provider = new(Result.Success(new ModelResponse("final", [])));
    RecordingEventLog log = new();
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 4000));
    Agent agent = new(provider, conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          EventLog = log,
          ContextMonitor = new PressuredMonitor(),
          ContextCompactor = new SummarizingCompactor(new TokenUsage(50, 100), "summarizer", "openrouter"),
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = Assert.Single(log.Events.OfType<AgentUsageRecorded>(),
        row => row.EffectiveCallClass.WireName == UsageCallClass.CompactionWire);
    Assert.Null(row.PromptPricePerToken);
    Assert.Null(row.WorkspaceId);
    Assert.Equal("openrouter", row.PricingProvider); // the summarizer's own provider still travels
  }

  [Fact]
  public void Create_NegativeTokenCount_IsADomainError()
  {
    // B10: a provider cannot report a negative count, and a fabricated negative would
    // poison every rollup it enters.
    Result<AgentUsageRecorded> result = AgentUsageRecorded.Create(AgentId.NewId(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), 0, new TokenUsage(-1, 10), UsageAttribution.Unattributed(UsageCallClass.MainLoop));

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidUsageCounts", result.Error.Code);
  }

  [Fact]
  public void Create_UnknownCallClass_IsRejectedAtTheWireBoundary()
  {
    // B10: the closed set is enforced where a persisted name arrives.
    Result<UsageCallClass> parsed = UsageCallClass.FromWire("model_selection");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidCallClass", parsed.Error.Code);
  }

  [Fact]
  public void Create_ValidUsage_CarriesEveryAttributionMember()
  {
    Result<AgentUsageRecorded> result = AgentUsageRecorded.Create(AgentId.NewId(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), 3, new TokenUsage(100, 20, 64, null, 7),
        new UsageAttribution(UsageCallClass.Compaction, "ws-1", "openrouter", Known));

    Assert.True(result.IsSuccess);
    AgentUsageRecorded row = result.Value;
    Assert.Same(UsageCallClass.Compaction, row.CallClass);
    Assert.Equal("ws-1", row.WorkspaceId);
    Assert.Equal("openrouter", row.PricingProvider);
    Assert.Equal(0.0000003m, row.PromptCacheReadPricePerToken);
    Assert.Equal(3, row.Iteration);
  }
}
