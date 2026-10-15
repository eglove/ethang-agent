using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Stream-activity stamping: the loop invokes the option per stream fragment
///     (content and reasoning alike), and legacy wiring stays byte-identical.
///     The supervisor-stamp integration is pinned by ChildSupervisorStreamStallTests;
///     here the option itself is under test.</summary>
public class AgentStreamStampTests
{
  private sealed class DualStreamFakeProvider(string[] contentDeltas, string[] reasoningDeltas) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      foreach (string delta in reasoningDeltas)
      {
        onReasoningDelta?.Invoke(delta);
      }

      foreach (string delta in contentDeltas)
      {
        onContentDelta?.Invoke(delta);
      }

      return Task.FromResult(Result.Success(new ModelResponse(string.Concat(contentDeltas), [])));
    }
  }

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task ContentDelta_InvokesOnStreamActivityWithContent()
  {
    List<string> stamps = [];
    Agent agent = new(new DualStreamFakeProvider(["hello"], []), new Conversation(),
        DefaultConfig, new ToolRegistry([]),
        new AgentOptions { OnStreamActivity = stamps.Add });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Contains("content", stamps);
  }

  [Fact]
  public async Task ReasoningDelta_InvokesOnStreamActivityWithReasoning()
  {
    List<string> stamps = [];
    Agent agent = new(new DualStreamFakeProvider([], ["thinking"]), new Conversation(),
        DefaultConfig, new ToolRegistry([]),
        new AgentOptions { OnStreamActivity = stamps.Add });

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Contains("reasoning", stamps);
  }

  [Fact]
  public async Task NoOption_NoCrash_LegacyByteIdentical()
  {
    Agent agent = new(new DualStreamFakeProvider(["hello"], []), new Conversation(),
        DefaultConfig, new ToolRegistry([]), options: null);

    Result<string> result = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("hello", result.Value);
  }

  [Fact]
  public async Task BothCallbackShapes_StampAndForward()
  {
    // When turn callbacks AND the stamp option exist, both fire: the stamp wraps,
    // never replaces, the callback.
    List<string> stamps = [];
    List<string> deltas = [];
    Agent agent = new(new DualStreamFakeProvider(["hello"], ["think"]), new Conversation(),
        DefaultConfig, new ToolRegistry([]),
        new AgentOptions { OnStreamActivity = stamps.Add });

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks { OnContentDelta = deltas.Add, OnReasoningDelta = deltas.Add },
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, stamps.Count); // one content, one reasoning
    Assert.Equal(2, deltas.Count); // both deltas forwarded
  }
}
