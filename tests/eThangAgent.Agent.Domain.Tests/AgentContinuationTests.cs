using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Length-truncated responses must not end the turn: the loop keeps the
///     partial message, appends a continuation nudge, and calls the provider again,
///     bounded by a per-turn auto-continuation cap.</summary>
public class AgentContinuationTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f).Value!;

  [Fact]
  public async Task LengthTruncatedAnswer_AutoContinues_ToCompletion()
  {
    StreamingFakeProvider provider = new(
        (["partial an"], new ModelResponse("partial answer cut off", [], FinishReason.Length)),
        ([" and the rest"], new ModelResponse("partial answer cut off and the rest", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, provider.Calls);
    // History order: partial assistant message, system continuation line, final answer.
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Equal(Role.Assistant, messages[^3].Role);
    Assert.Equal("partial answer cut off", messages[^3].Content);
    Assert.Equal(Role.System, messages[^2].Role);
    Assert.Contains("output limit", messages[^2].Content, StringComparison.Ordinal);
    Assert.Equal(Role.Assistant, messages[^1].Role);
    Assert.Equal("partial answer cut off and the rest", messages[^1].Content);
  }

  [Fact]
  public async Task LengthTruncation_BeyondCap_Fails_WithMaxOutputContinuations()
  {
    AlwaysTruncatedProvider alwaysTruncated = new();
    Agent agent = new(alwaysTruncated, new Conversation(), DefaultConfig,
        new ToolRegistry([]), new AgentOptions { MaxAutoContinuations = 2 });

    Result<string> result = await agent.SendMessage("hi", default);

    Assert.False(result.IsSuccess);
    Assert.Equal("MaxOutputContinuations", result.Error!.Code);
    Assert.Equal(3, alwaysTruncated.Calls); // initial + 2 continuations
  }

  [Fact]
  public async Task StopFinishReason_DoesNotContinue()
  {
    StreamingFakeProvider provider = new(
        ([], new ModelResponse("complete answer", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default);

    Assert.True(result.IsSuccess);
    Assert.Equal(1, provider.Calls);
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Role == Role.System);
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

  private sealed class AlwaysTruncatedProvider : IModelProvider
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
      return Task.FromResult(Result.Success(
          new ModelResponse($"fragment {Calls}", [], FinishReason.Length)));
    }
  }
}
