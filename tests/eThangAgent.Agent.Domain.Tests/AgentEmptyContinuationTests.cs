using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>An empty Length-truncated response (the model burned its whole output
///     budget on hidden reasoning and produced no text) must NOT get the generic
///     'continue exactly where you stopped' nudge: there is nothing to continue,
///     and the generic prompt invites a full re-derivation — the 2026-10-08 stalls
///     where subagents sat in consecutive multi-minute provider calls producing
///     nothing. The empty case gets its own nudge naming the situation and what
///     to do instead (answer directly, skip the re-derivation).</summary>
public class AgentEmptyContinuationTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task EmptyLengthTruncation_Nudges_With_EmptySpecificPrompt()
  {
    StreamingFakeProvider provider = new(
        ([], new ModelResponse("", [], FinishReason.Length)),
        ([], new ModelResponse("the actual answer", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("hi", default, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, provider.Calls);
    IReadOnlyList<Message> messages = agent.Conversation.Messages;
    Assert.Equal(Role.System, messages[^2].Role);
    string nudge = messages[^2].Content;
    Assert.Contains("no visible text", nudge, StringComparison.Ordinal);
    Assert.DoesNotContain("Continue exactly where you stopped", nudge, StringComparison.Ordinal);
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
}
