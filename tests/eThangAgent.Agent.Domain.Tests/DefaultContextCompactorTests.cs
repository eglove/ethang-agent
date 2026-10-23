using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

public class DefaultContextCompactorTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  private static readonly ModelConfig Serving = ModelConfig.Create("serving", null, 100, 0.5f, 1000).Value!;

  private sealed class RecordingFactory(ModelConfig? served) : IModelProviderFactory
  {
    public ModelConfig? LastConfig { get; private set; } = served;
    public IModelProvider Provider { get; } = new ScriptedProvider();

    public IModelProvider Create(ModelConfig config)
    {
      LastConfig = config;
      return Provider;
    }
  }

  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);
    public ModelRequest? LastRequest { get; private set; }

    public void Enqueue(Result<ModelResponse> response) => _responses.Enqueue(response);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      LastRequest = request;
      return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : Result.Success(new ModelResponse("summary text", [])));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private static Message User(string text) => new(Role.User, text, T);

  /// <summary>A conversation whose plan evicts the three messages after the protected
  /// head at window 1000 (target 40%): the first user message stays, the 15000-char
  /// middle is evicted, and six small recent exchanges form the kept tail.</summary>
  private static Conversation ConversationWithEvictableMiddle()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("task"); // protected head: the original task statement
    conversation.AddAssistantMessage(new string('x', 5000));
    conversation.AddUserMessage(new string('y', 5000));
    conversation.AddAssistantMessage(new string('z', 5000));
    for (int i = 0; i < 6; i++)
    {
      conversation.AddUserMessage($"recent {i}");
      conversation.AddAssistantMessage($"answer {i}");
    }

    return conversation;
  }

  [Fact]
  public async Task CompactAsync_NullWindowConfig_ReturnsUnknownContextWindowFailure()
  {
    // A serving model whose window is not yet catalog-resolved cannot plan an
    // eviction: the compactor fails with a named code instead of guessing a window.
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 1500));
    conversation.AddAssistantMessage(new string('y', 1500));

    ModelConfig unknownWindow = ModelConfig.Create("serving", null, 100, 0.5f, null).Value!;
    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, unknownWindow, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("UnknownContextWindow", outcome.Error.Code);
  }

  [Fact]
  public async Task CompactAsync_EvictsPrefix_WritesSummaryWithIsSummaryFlag()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null); // fallback: serving model writes

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess);
    Assert.Equal(3, outcome.Value.MessagesEvicted); // the assistant/user/assistant middle; head protected
    Assert.True(conversation.Messages[0].IsSummary);
    Assert.Contains("summary text", conversation.Messages[0].Content, StringComparison.Ordinal);
    Assert.Equal(14, conversation.Messages.Count); // summary(1) + protected head(1) + kept tail(12)
    Assert.Equal("task", conversation.Messages[1].Content); // the protected head survives compaction
  }

  [Fact]
  public async Task CompactAsync_SummarizerFactoryNull_FallsBackToServingModel()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    _ = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.Equal(Serving.ModelId, factory.LastConfig!.ModelId);
  }

  [Fact]
  public async Task CompactAsync_NothingToEvict_FailsWithCompactionImpossible()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("only");
    conversation.AddAssistantMessage("one");

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("CompactionImpossible", outcome.Error.Code);
  }

  [Fact]
  public async Task CompactAsync_EmptySummary_Fails_ConversationUntouched()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    ((ScriptedProvider)factory.Provider).Enqueue(Result.Success(new ModelResponse("   ", [])));
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("EmptySummary", outcome.Error.Code);
    Assert.Equal(16, conversation.Messages.Count); // untouched
  }

  [Fact]
  public void Render_PinsExactWireStrings()
  {
    List<Message> evicted =
    [
      User("do the thing"),
      new(Role.Assistant, "", T, [new ToolCall("c1", "read", "{}")]),
      new(Role.Tool, "file contents", T, ToolCallId: "c1"),
    ];
    List<Message> keptTail = [User("next")];

    string rendered = DefaultContextCompactor.Render(evicted, keptTail);

    Assert.Contains("[User] do the thing", rendered, StringComparison.Ordinal);
    Assert.Contains("tool_call(c1): read({})", rendered, StringComparison.Ordinal);
    Assert.Contains("[Tool] file contents [answers c1]", rendered, StringComparison.Ordinal);
    Assert.Contains("## Kept tail", rendered, StringComparison.Ordinal);
  }

  /// <summary>The handoff decision rides the existing ceiling path: a prefix-eviction
  ///     compaction (issue #234) requests a session handoff; the tail-compression
  ///     fallback (no summary produced) does not; every CompactAsync call re-decides.</summary>
  [Fact]
  public async Task CompactAsync_PrefixEviction_SetsHandoffRequested()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess);
    Assert.True(compactor.HandoffRequested);
  }

  [Fact]
  public async Task CompactAsync_TailCompressionFallback_DoesNotSetHandoffRequested()
  {
    // The tail-compression fallback produces no summary, so a handoff cannot ride it.
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    for (int i = 0; i < 3; i++)
    {
      conversation.AddUserMessage($"kept user {i}");
      conversation.AddAssistantMessage("", [new ToolCall($"c{i}", "read", "{}")]);
      conversation.AddToolResult($"c{i}", new string('r', 800));
    }

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(compactor.HandoffRequested);
  }

  [Fact]
  public async Task CompactAsync_FailedEviction_DoesNotSetHandoffRequested()
  {
    // A failed eviction (empty summary) produced no summary: no handoff decision.
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    ((ScriptedProvider)factory.Provider).Enqueue(Result.Success(new ModelResponse("   ", [])));
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.False(compactor.HandoffRequested);
  }

  [Fact]
  public async Task CompactAsync_SecondCall_ReDecidesHandoffFlag()
  {
    // Each CompactAsync call re-decides: a handoff-requesting eviction followed by a
    // tail-compression fallback resets the flag to false.
    Conversation first = ConversationWithEvictableMiddle();
    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    _ = await compactor.CompactAsync(first, Serving, TestContext.Current.CancellationToken);
    Assert.True(compactor.HandoffRequested);

    Conversation second = new();
    second.AddUserMessage("task");
    for (int i = 0; i < 3; i++)
    {
      second.AddUserMessage($"kept user {i}");
      second.AddAssistantMessage("", [new ToolCall($"c{i}", "read", "{}")]);
      second.AddToolResult($"c{i}", new string('r', 800));
    }

    Result<CompactionOutcome> fallback = await compactor.CompactAsync(second, Serving, TestContext.Current.CancellationToken);

    Assert.True(fallback.IsSuccess, fallback.Error?.Code + ": " + fallback.Error?.Message);
    Assert.False(compactor.HandoffRequested);
  }
}
