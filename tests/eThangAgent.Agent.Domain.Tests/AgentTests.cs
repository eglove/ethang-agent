using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.AgentDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class AgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    [Fact]
    public void Agent_DefaultConstruction_GeneratesDistinctRootIds()
    {
        var first = new Agent(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
            new ToolRegistry([]));
        var second = new Agent(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
            new ToolRegistry([]));

        Assert.NotEqual(default(AgentId), first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(0, first.Depth);
        Assert.Equal(0, second.Depth);
    }

    [Fact]
    public void Agent_ExplicitIdAndDepth_ExposedOnAggregate()
    {
        var id = AgentId.NewId();
        var agent = new Agent(new ScriptedModelProvider(), new Conversation(), DefaultConfig,
            new ToolRegistry([]), id: id, depth: 2);

        Assert.Equal(id, agent.Id);
        Assert.Equal(2, agent.Depth);
    }

    [Fact]
    public async Task SendMessage_OnSuccess_AddsBothMessages()
    {
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse("Hello back", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("Hi");

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello back", result.Value);
        Assert.Equal(2, agent.Conversation.Messages.Count);
        Assert.Equal(Role.Assistant, agent.Conversation.Messages[1].Role);
    }

    [Fact]
    public async Task SendMessage_ProviderFailure_Propagates()
    {
        var err = new Error("Test", "fail");
        var provider = new ScriptedModelProvider(Result<ModelResponse>.Failure(err));
        var agent = new Agent(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

        var result = await agent.SendMessage("Hi");

        Assert.False(result.IsSuccess);
        Assert.Equal(err, result.Error);
    }

    [Fact]
    public async Task SendMessage_ToolCall_ExecutesAndFeedsResultBack()
    {
        var fakeTool = new FakeTool("read", "file content");
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "read", "{\"p\":\"f\"}")])),
            Result<ModelResponse>.Success(new ModelResponse("done", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([fakeTool]));

        var result = await agent.SendMessage("read file");

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
        var provider = new ScriptedModelProvider(
            Result<ModelResponse>.Success(new ModelResponse(null,
                [new ToolCallRequest("call_1", "nope", "{}")])),
            Result<ModelResponse>.Success(new ModelResponse("final", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([]));

        var result = await agent.SendMessage("hi");

        Assert.True(result.IsSuccess);
        var toolMsg = agent.Conversation.Messages[2];
        Assert.Equal(Role.Tool, toolMsg.Role);
        Assert.Contains("Unknown tool", toolMsg.Content);
    }

    [Fact]
    public async Task SendMessage_MaxIterationsExhausted_ReturnsFailure()
    {
        var provider = new ScriptedModelProvider(
            Enumerable.Repeat(
                Result<ModelResponse>.Success(new ModelResponse(null,
                    [new ToolCallRequest("c1", "loopy", "{}")])),
                10).ToArray());
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([new FakeTool("loopy", "again")]), maxToolIterations: 10);

        var result = await agent.SendMessage("hi");

        Assert.False(result.IsSuccess);
        Assert.Equal("MaxToolIterations", result.Error!.Code);
    }

    [Fact]
    public async Task SendMessage_WithSystemPrompt_SuppliesItToProvider()
    {
        var provider = new CapturingProvider();
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([]), new StaticPromptProvider("guide text"));

        await agent.SendMessage("hi");

        Assert.Equal("guide text", provider.LastRequest!.SystemPrompt);
    }

    private sealed class CapturingProvider : IModelProvider
    {
        public ModelRequest? LastRequest { get; private set; }

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Result<ModelResponse>.Success(new ModelResponse("ok", [])));
        }
    }

    private sealed class ScriptedModelProvider : IModelProvider
    {
        private readonly Queue<Result<ModelResponse>> _responses;
        public ScriptedModelProvider(params Result<ModelResponse>[] responses)
            => _responses = new Queue<Result<ModelResponse>>(responses);

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
                : Result<ModelResponse>.Success(new ModelResponse("fin", [])));
    }

    private sealed class FakeTool : ITool
    {
        private readonly string _resultContent;
        public ToolDefinition Definition { get; }
        public FakeTool(string name, string resultContent)
        {
            Definition = new ToolDefinition(name, "desc", []);
            _resultContent = resultContent;
        }
        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => Task.FromResult(new ToolResult(_resultContent, false));
    }
}
