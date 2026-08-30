using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

public class StreamingAgentTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>Streams scripted delta batches per provider call, then yields the assembled
  ///     response. SendAsync throws: the agent loop must use the streaming path.</summary>
  internal sealed class StreamingFakeProvider(params (string[] Deltas, ModelResponse Response)[] turns) : IModelProvider
  {
    private readonly Queue<(string[] Deltas, ModelResponse Response)> _turns = new(turns);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      (string[]? deltas, ModelResponse? response) = _turns.Dequeue();
      foreach (string delta in deltas)
      {
        onContentDelta?.Invoke(delta);
      }

      return Task.FromResult(Result.Success(response));
    }
  }

  [Fact]
  public async Task Deltas_FlowThrough_InOrder_AndIterationEndFiresPerProviderCall()
  {
    StreamingFakeProvider provider = new(
        (["think ", "first"], new ModelResponse("think first",
            [new ToolCallRequest("c1", "read", "{}")])),
        ([], new ModelResponse(null, [new ToolCallRequest("c2", "read", "{}")])),
        (["final"], new ModelResponse("final", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FakeTool("read", "file content")]));
    List<string> deltas = [];
    int iterations = 0;

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks
        {
          OnContentDelta = deltas.Add,
          OnIterationEnd = () => iterations++,
        }, ct: TestContext.Current.CancellationToken);

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
    Agent agent = new(new FakeProvider(
            Result.Success(new ModelResponse(null,
                [new ToolCallRequest("c1", "read", "{}")])),
            Result.Success(new ModelResponse("done", []))),
        new Conversation(), DefaultConfig, new ToolRegistry([new FakeTool("read", "ok")]));
    List<string> deltas = [];
    int iterations = 0;

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks { OnContentDelta = deltas.Add, OnIterationEnd = () => iterations++ }, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("done", result.Value);
    Assert.Empty(deltas);
    Assert.Equal(2, iterations);
  }
}
