using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f).Value!;

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
    const int rounds = 151;
    Result<ModelResponse>[] responses =
    [
      .. Enumerable.Range(0, rounds)
              .Select(_ => Result.Success(new ModelResponse(null,
                  [new ToolCallRequest("c1", "loopy", "{}")])))
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
}
