using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>An EMPTY answer is not a finished turn. A provider can return no content and no
///     tool calls for reasons a harness cannot see - a model that stalled, a free tier that
///     truncated the stream, a gateway that dropped the body and still sent its terminator -
///     and the 2026-11-16 chain sessions sat idle for minutes until a human typed 'continue'.
///     The loop treats a content-free answer like a truncated one: keep it in history, nudge
///     once, and call the provider again, bounded by the same per-turn budget.</summary>
public class AgentEmptyAnswerTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task EmptyAnswer_WithNoToolCalls_Nudges_And_CallsProviderAgain()
  {
    StreamingFakeProvider provider = new(
        ([], new ModelResponse("", [])),
        ([], new ModelResponse("the actual answer", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
    Assert.Equal("the actual answer", result.Value);
    Assert.Equal(2, provider.Calls);
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Equal(Role.System, messages[^2].Role);
    Assert.Contains("empty", messages[^2].Content, StringComparison.Ordinal);
    Assert.Equal(Role.Assistant, messages[^1].Role);
  }

  [Fact]
  public async Task EmptyAnswers_BeyondTheBudget_Fail_InsteadOfEndingTheTurn()
  {
    AlwaysEmptyProvider provider = new();
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]),
        new AgentOptions { MaxAutoContinuations = 2 });

    Result<string> result = await agent.SendMessage("hi", default, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("MaxEmptyAnswers", result.Error.Code);
    Assert.Equal(3, provider.Calls); // initial + 2 nudges, then the turn fails loudly
  }

  [Fact]
  public async Task EmptyAnswer_InALaterTurn_AlsoContinues()
  {
    StreamingFakeProvider provider = new(
        ([], new ModelResponse("first answer", [])),
        ([], new ModelResponse("", [])),
        ([], new ModelResponse("second answer", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> first = await agent.SendMessage("one", default, ct: TestContext.Current.CancellationToken);
    Result<string> second = await agent.SendMessage("two", default, ct: TestContext.Current.CancellationToken);

    Assert.Equal("first answer", first.Value);
    Assert.True(second.IsSuccess, second.IsSuccess ? "" : second.Error.Code);
    Assert.Equal("second answer", second.Value);
    Assert.Equal(3, provider.Calls);
  }

  [Fact]
  public async Task EmptyTruncatedAnswer_KeepsTheLengthNudge()
  {
    StreamingFakeProvider provider = new(
        ([], new ModelResponse("", [], FinishReason.Length)),
        ([], new ModelResponse("the actual answer", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Error.Code);
    Assert.Equal(2, provider.Calls);
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Contains("no visible text", messages[^2].Content, StringComparison.Ordinal);
  }

  private sealed class StreamingFakeProvider(params (string[] Deltas, ModelResponse Response)[] turns)
      : IModelProvider
  {
    private readonly Queue<(string[] Deltas, ModelResponse Response)> _turns = new(turns);
    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      Calls++;
      (string[]? deltas, ModelResponse? response) = _turns.Dequeue();
      foreach (string delta in deltas)
      {
        onContentDelta?.Invoke(delta);
      }

      return Task.FromResult(Result.Success(response));
    }
  }

  private sealed class AlwaysEmptyProvider : IModelProvider
  {
    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new ModelResponse("", [])));
    }
  }
}
