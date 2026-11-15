using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The transient-fault rewind: the text a failed attempt streamed is withdrawn
///     BEFORE the retry notice and the retry wait, so the only answer an observer ends up
///     showing is the successful one. The withdrawal is an OBSERVER fact - a failed attempt
///     never entered the conversation - which is why no request is rewritten.</summary>
public class AgentStreamRewindTests
{
  private sealed record Attempt(string[] Content, string[] Reasoning, string? FailCode, string? Answer);

  /// <summary>Replays one scripted attempt per provider call. Exhausting the script fails with
  ///     NO deltas, so a later attempt shapes no window and must not be rewound again.</summary>
  private sealed class ScriptedStreamProvider(params Attempt[] attempts) : IModelProvider
  {
    private int _next;

    public int Calls { get; private set; }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("the agent loop must use SendStreamingAsync.");

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
      Calls++;
      if (_next >= attempts.Length)
      {
        return Task.FromResult(Result.Failure<ModelResponse>(
            new DomainError("StreamInterrupted", "the stream ended without its terminator.")));
      }

      Attempt attempt = attempts[_next++];
      foreach (string delta in attempt.Reasoning)
      {
        onReasoningDelta?.Invoke(delta);
      }

      foreach (string delta in attempt.Content)
      {
        onContentDelta?.Invoke(delta);
      }

      return Task.FromResult(attempt.FailCode is { } code
          ? Result.Failure<ModelResponse>(new DomainError(code, "the stream ended without its terminator."))
          : Result.Success(new ModelResponse(attempt.Answer ?? string.Empty, [])));
    }
  }

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  /// <summary>The retry loop's delay seam, substituted so no test waits a real second.</summary>
  private static AgentOptions NoWaiting =>
      new() { TurnRetryDelay = (_, _) => Task.CompletedTask };

  private static Agent Build(IModelProvider provider, Conversation conversation) =>
      new(provider, conversation, DefaultConfig, new ToolRegistry([]), NoWaiting);

  /// <summary>Names the production change that would fail this test: recording the failure or
  ///     sending the retry before withdrawing the prefix leaves that prefix on screen above the
  ///     retry line.</summary>
  [Fact]
  public async Task TransientFault_WithdrawsStreamedTextBeforeTheRetryNotice()
  {
    List<string> observed = [];
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt(["Hello, wor"], [], "StreamInterrupted", null),
            new Attempt(["Hi there."], [], null, "Hi there.")),
        new Conversation());

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnContentDelta: _ => observed.Add("delta"),
            OnStreamRewind: () => observed.Add("rewind"),
            OnStreamRetrying: (_, _, _) => observed.Add("retry")),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("Hi there.", result.Value, StringComparer.Ordinal);
    Assert.Equal(["delta", "rewind", "retry", "delta"], observed);
  }

  /// <summary>An attempt that streamed nothing is never withdrawn: the count is what gates
  ///     the rewind, not the fact that a retry happened.</summary>
  [Fact]
  public async Task FaultBeforeAnyDelta_RewindsNothing()
  {
    List<string> observed = [];
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt([], [], "ProviderError", null),
            new Attempt([], [], null, "recovered")),
        new Conversation());

    _ = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnContentDelta: _ => { },
            OnStreamRewind: () => observed.Add("rewind")),
        ct: TestContext.Current.CancellationToken);

    Assert.Empty(observed);
  }

  /// <summary>One withdrawal per failed attempt that streamed: an exhausted turn is rewound
  ///     for its first attempt only, because the attempts after it delivered no text.</summary>
  [Fact]
  public async Task ExhaustedTurn_WithdrawsOnlyWhatWasStreamed()
  {
    List<string> rewind = [];
    List<string> retry = [];
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt(["Hel"], [], "StreamInterrupted", null)),
        new Conversation());

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnContentDelta: _ => { },
            OnStreamRewind: () => rewind.Add("rewind"),
            OnStreamRetrying: (_, _, _) => retry.Add("retry")),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code, StringComparer.Ordinal);
    _ = Assert.Single(rewind);
    Assert.Equal(5, retry.Count);
  }

  /// <summary>A model verdict is not a fault a retry can fix: the text that attempt streamed
  ///     is the model's own output and stays exactly as it arrived.</summary>
  [Fact]
  public async Task ModelVerdict_AfterStreamedText_RewindsNothing()
  {
    List<string> observed = [];
    Agent agent = Build(
        new ScriptedStreamProvider(new Attempt(["Hel"], [], "RateLimited", null)),
        new Conversation());

    Result<string> result = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnContentDelta: _ => { },
            OnStreamRewind: () => observed.Add("rewind")),
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Empty(observed);
  }

  /// <summary>Reasoning streams into the same open block the transcript withdraws, so a
  ///     reasoning-only attempt is rewound like a content one.</summary>
  [Fact]
  public async Task TransientFault_AfterReasoningOnly_Rewinds()
  {
    List<string> observed = [];
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt([], ["let me think"], "ProviderTimeout", null),
            new Attempt([], [], null, "done")),
        new Conversation());

    _ = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnReasoningDelta: _ => { },
            OnStreamRewind: () => observed.Add("rewind")),
        ct: TestContext.Current.CancellationToken);

    _ = Assert.Single(observed);
  }

  /// <summary>The duplicate is an OBSERVER fact: a failed attempt writes no assistant message,
  ///     so the conversation the retry builds from carries no trace of the withdrawn text and
  ///     the retry's request is byte-identical to the failed one.</summary>
  [Fact]
  public async Task FailedAttempt_LeavesNoAssistantMessageInTheConversation()
  {
    Conversation conversation = new();
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt(["Hello, wor"], [], "StreamInterrupted", null)),
        conversation);

    _ = await agent.SendMessage("hi",
        new TurnCallbacks(
            OnContentDelta: _ => { },
            OnStreamRewind: () => { }),
        ct: TestContext.Current.CancellationToken);

    Assert.DoesNotContain(conversation.Messages, m => m.Role is Role.Assistant);
    Assert.DoesNotContain(conversation.Messages,
        m => m.Content.Contains("Hello, wor", StringComparison.Ordinal));
  }

  /// <summary>No wiring at all keeps the legacy shape: the turn fails with its own code and no
  ///     observer is required to exist.</summary>
  [Fact]
  public async Task NoCallbacks_TransientFault_StillFails()
  {
    Agent agent = Build(
        new ScriptedStreamProvider(
            new Attempt(["Hel"], [], "StreamInterrupted", null)),
        new Conversation());

    Result<string> result = await agent.SendMessage("hi",
        ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code, StringComparer.Ordinal);
  }
}
