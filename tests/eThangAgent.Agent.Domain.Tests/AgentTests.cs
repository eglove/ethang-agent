using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public void Agent_DefaultConstruction_GeneratesDistinctRootIds()
  {
    Agent first = new(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
        new ToolRegistry([]));
    Agent second = new(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Assert.NotEqual(default, first.Id);
    Assert.NotEqual(first.Id, second.Id);
    Assert.Equal(0, first.Depth);
    Assert.Equal(0, second.Depth);
  }

  [Fact]
  public void Agent_ExplicitIdAndDepth_ExposedOnAggregate()
  {
    AgentId id = AgentId.NewId();
    Agent agent = new(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
        new ToolRegistry([]), new AgentOptions { Id = id, Depth = 2 });

    Assert.Equal(id, agent.Id);
    Assert.Equal(2, agent.Depth);
  }

  [Fact]
  public async Task SendMessage_OnSuccess_AddsBothMessages()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse("Hello back", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("Hello back", result.Value);
    Assert.Equal(2, agent.Conversation.Messages.Count);
    Assert.Equal(Role.Assistant, agent.Conversation.Messages[1].Role);
  }

  [Fact]
  public async Task SendMessage_ProviderFailure_Propagates()
  {
    DomainError err = new("Test", "fail");
    ScriptedModelProvider provider = new(Result.Failure<ModelResponse>(err));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(err, result.Error);
  }

  [Fact]
  public async Task SendMessage_ToolCall_ExecutesAndFeedsResultBack()
  {
    FakeTool fakeTool = new("read", "file content");
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "read", /*lang=json,strict*/ "{\"p\":\"f\"}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([fakeTool]));

    Result<string> result = await agent.SendMessage("read file", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
    Assert.Equal(4, agent.Conversation.Messages.Count);
    Assert.Equal(Role.User, agent.Conversation.Messages[0].Role);
    Assert.Equal(Role.Assistant, agent.Conversation.Messages[1].Role);
    Assert.Equal(Role.Tool, agent.Conversation.Messages[2].Role);
    Assert.Equal("file content", agent.Conversation.Messages[2].Content);
    Assert.Equal(Role.Assistant, agent.Conversation.Messages[3].Role);
    Assert.Equal("done", agent.Conversation.Messages[3].Content);
  }

  [Fact]
  public async Task SendMessage_UnknownTool_ReturnsErrorToolResult()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "nope", "{}")])),
        Result.Success(new ModelResponse("final", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMsg = agent.Conversation.Messages[2];
    Assert.Equal(Role.Tool, toolMsg.Role);
    Assert.Contains("Unknown tool", toolMsg.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendMessage_ToolLoop_ConvergesBeyondAnyIterationCap()
  {
    // The tool loop has no iteration limit: it keeps calling tools until the
    // model answers without tool calls. 151 rounds — far beyond any former cap.
    // The arguments vary per round: the repeat guard ends a turn of byte-identical
    // calls (critical at the 13th, turn over on the 14th), and this test pins the
    // unbounded loop, which must stay reachable for a varied-call loop.
    const int rounds = 151;
    Result<ModelResponse>[] responses =
    [
      .. Enumerable.Range(0, rounds)
              .Select(i => Result.Success(new ModelResponse(null,
                  [new ToolCallRequest("c" + i, "loopy", "{\"round\":" + i + "}")])))
,
      Result.Success(new ModelResponse("finally done", [])),
    ];
    ScriptedModelProvider provider = new(responses);
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("loopy", "again")]));

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("finally done", result.Value);
  }

  [Fact]
  public async Task SendMessage_ZeroToolCallTurn_LastTurnToolCallsIsZero()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    _ = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.Equal(0, agent.LastTurnToolCalls);
  }

  [Fact]
  public async Task SendMessage_TurnWithThreeToolCalls_CounterReflectsThem()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null,
        [
            new ToolCallRequest("c1", "t", "{}"),
                new ToolCallRequest("c2", "t", "{}"),
                new ToolCallRequest("c3", "t", "{}"),
        ])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("t", "ok")]));

    _ = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.Equal(3, agent.LastTurnToolCalls);
  }

  [Fact]
  public async Task SendMessage_FailedTurnWithoutToolCalls_CounterResetsToZero()
  {
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("c1", "t", "{}")])),
        Result.Success(new ModelResponse("mid", [])),
        Result.Failure<ModelResponse>(new DomainError("Boom", "provider down")));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("t", "ok")]));

    Result<string> first = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.Equal(1, agent.LastTurnToolCalls);

    Result<string> second = await agent.SendMessage("again", ct: TestContext.Current.CancellationToken);

    Assert.False(second.IsSuccess);
    Assert.Equal(0, agent.LastTurnToolCalls);
  }

  [Fact]
  public async Task SendMessage_WithSystemPrompt_SuppliesItToProvider()
  {
    CapturingProvider provider = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]), new AgentOptions { SystemPrompt = new StaticPromptProvider("guide text") });

    _ = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.Equal("guide text", provider.LastRequest!.SystemPrompt);
  }

  private sealed class CapturingProvider : IModelProvider
  {
    public ModelRequest? LastRequest { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
      LastRequest = request;
      return Task.FromResult(Result.Success(new ModelResponse("ok", [])));
    }
  }

  [Fact]
  public async Task ToolCallbacks_ReportBatchPosition_OnToolCall()
  {
    // Two tool calls in one provider response: the second iteration answers plain text.
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null,
            [new ToolCallRequest("c1", "t1", "{}"), new ToolCallRequest("c2", "t2", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("t1", "r1"), new FakeTool("t2", "r2")]));

    List<(string Name, int Index, int Count)> calls = [];
    TurnCallbacks callbacks = new(OnToolCall: (name, _, index, count) => calls.Add((name, index, count)));

    _ = await agent.SendMessage("Hi", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, calls.Count);
    Assert.Equal(("t1", 1, 2), calls[0]);
    Assert.Equal(("t2", 2, 2), calls[1]);
  }

  [Fact]
  public async Task ToolCallbacks_SingleCallBatch_CountsOne()
  {
    ScriptedModelProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "t1", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("t1", "r1")]));

    List<(int Index, int Count)> calls = [];
    TurnCallbacks callbacks = new(OnToolCall: (_, _, index, count) => calls.Add((index, count)));

    _ = await agent.SendMessage("Hi", callbacks: callbacks, ct: TestContext.Current.CancellationToken);

    _ = Assert.Single(calls);
    Assert.Equal((1, 1), calls[0]);
  }

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class FakeTool(string name, string resultContent) : ITool
  {
    private readonly string _resultContent = resultContent;
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(_resultContent, false));
  }

  private sealed class StubProvider : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ModelResponse("final", [])));

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private sealed class ThresholdMonitor(double utilization, int window = 1000) : IContextMonitor
  {
    public ContextStatus Status { get; set; } =
        new((int)(utilization * window / 100.0), (long)(utilization * window / 100.0), 0, window, utilization);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
    }
  }

  /// <summary>The compaction handoff seam (issue #234): a compactor whose outcome IS a
  /// handoff (carrying the summary; the conversation is NOT compacted in place) leaves
  /// HandoffPending true after the turn ends; the composition clears it via
  /// ClearHandoffPending. An ordinary compaction outcome leaves the flag false.</summary>
  private sealed class HandoffCompactor(bool handoff) : IContextCompactor
  {
    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      return Task.FromResult(handoff
          ? Result.Success(CompactionOutcome.Handoff("the handoff summary", new TokenUsage(50, 100)))
          : Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1, new TokenUsage(50, 100))));
    }
  }

  [Fact]
  public async Task Handoff_HandoffOutcome_SetsHandoffPendingAndAppendsNewHandoffLine()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800)); // believable report: 800 tokens at 4 chars/token
    HandoffCompactor compactor = new(handoff: true);
    Agent agent = new(new StubProvider(), conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(agent.HandoffPending);
    // The handoff line is the NEW verbatim text: it names NO session id (the host cannot
    // know the new id yet; the app posts the actual id as a notice after launching).
    Assert.Contains(conversation.Messages, m =>
        m.Role is Role.System && m.Content == "[context handoff: the conversation continues in a new session.]");
  }

  [Fact]
  public async Task Handoff_ClearHandoffPending_ResetsFlag()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800));
    HandoffCompactor compactor = new(handoff: true);
    Agent agent = new(new StubProvider(), conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    _ = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);
    Assert.True(agent.HandoffPending);

    agent.ClearHandoffPending();

    Assert.False(agent.HandoffPending);
  }

  [Fact]
  public async Task Handoff_NonHandoffCompaction_LeavesHandoffPendingFalse()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800));
    HandoffCompactor compactor = new(handoff: false);
    Agent agent = new(new StubProvider(), conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.False(agent.HandoffPending);
  }

  /// <summary>A handoff outcome did NOT compact the conversation: there is no summary
  /// message in it, so the summary-surfacing part of NotifyCompacted must not run - the
  /// OnSystemMessage callback must not see a summary line from a handoff.</summary>
  [Fact]
  public async Task Handoff_HandoffOutcome_DoesNotSurfaceASummaryMessage()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 800));
    HandoffCompactor compactor = new(handoff: true);
    Agent agent = new(new StubProvider(), conversation, DefaultConfig, new ToolRegistry([]),
        new AgentOptions
        {
          ContextMonitor = new ThresholdMonitor(80.0),
          ContextCompactor = compactor,
        });
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("go",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(agent.HandoffPending);
    Assert.DoesNotContain(surfaced, s => s.Contains("Conversation summary", StringComparison.Ordinal));
    // The conversation itself carries no summary message: the handoff did not compact.
    Assert.DoesNotContain(conversation.Messages, m => m.IsSummary);
  }
}
