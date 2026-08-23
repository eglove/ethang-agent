using eThangAgent.ModelDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.AgentDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class StreamingAgentTests
{
    private static ModelConfig DefaultConfig =>
        ModelConfig.Create("test-model", 100, 0.5f).Value!;

    /// <summary>Streams scripted delta batches per provider call, then yields the assembled
    ///     response. SendAsync throws: the agent loop must use the streaming path.</summary>
    public sealed class StreamingFakeProvider : IModelProvider
    {
        private readonly Queue<(string[] Deltas, ModelResponse Response)> _turns;

        public StreamingFakeProvider(params (string[] Deltas, ModelResponse Response)[] turns)
            => _turns = new Queue<(string[], ModelResponse)>(turns);

        public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

        public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
            Action<string>? onContentDelta = null,
            Action<string>? onReasoningDelta = null,
            CancellationToken ct = default)
        {
            var (deltas, response) = _turns.Dequeue();
            foreach (var delta in deltas)
                onContentDelta?.Invoke(delta);
            return Task.FromResult(Result<ModelResponse>.Success(response));
        }
    }

    [Fact]
    public async Task Deltas_FlowThrough_InOrder_AndIterationEndFiresPerProviderCall()
    {
        var provider = new StreamingFakeProvider(
            (["think ", "first"], new ModelResponse("think first",
                [new ToolCallRequest("c1", "read", "{}")])),
            ([], new ModelResponse(null, [new ToolCallRequest("c2", "read", "{}")])),
            (["final"], new ModelResponse("final", [])));
        var agent = new Agent(provider, new Conversation(), DefaultConfig,
            new ToolRegistry([new FakeTool("read", "file content")]));
        var deltas = new List<string>();
        var iterations = 0;

        var result = await agent.SendMessage("hi", default,
            onContentDelta: deltas.Add,
            onIterationEnd: () => iterations++);

        Assert.True(result.IsSuccess);
        Assert.Equal("final", result.Value);
        Assert.Equal(["think ", "first", "final"], deltas); // interstitial text streams too
        Assert.Equal(3, iterations);
        // Conversation integrity unchanged: user, assistant(tc), tool, assistant(tc), tool, assistant.
        Assert.Equal(6, agent.Conversation.Messages.Count);
    }

    [Fact]
    public async Task NonStreamingProvider_ViaDefaultMethod_StillSucceedsWithoutCallbacks()
    {
        var agent = new Agent(new FakeProvider(
                Result<ModelResponse>.Success(new ModelResponse(null,
                    [new ToolCallRequest("c1", "read", "{}")])),
                Result<ModelResponse>.Success(new ModelResponse("done", []))),
            new Conversation(), DefaultConfig, new ToolRegistry([new FakeTool("read", "ok")]));
        var deltas = new List<string>();
        var iterations = 0;

        var result = await agent.SendMessage("hi", default, onContentDelta: deltas.Add, onIterationEnd: () => iterations++);

        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.Value);
        Assert.Empty(deltas);
        Assert.Equal(2, iterations);
    }
}
