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

  [Fact]
  public async Task CompactAsync_EvictsPrefix_WritesSummaryWithIsSummaryFlag()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 1500)); // forces an eviction plan at window 1000
    conversation.AddAssistantMessage(new string('y', 1500));
    for (int i = 0; i < 6; i++)
    {
      conversation.AddUserMessage($"recent {i}");
      conversation.AddAssistantMessage($"answer {i}");
    }

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null); // fallback: serving model writes

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess);
    Assert.Equal(2, outcome.Value.MessagesEvicted);
    Assert.True(conversation.Messages[0].IsSummary);
    Assert.Contains("summary text", conversation.Messages[0].Content, StringComparison.Ordinal);
    Assert.Equal(13, conversation.Messages.Count); // summary(1) + kept tail (12)
  }

  [Fact]
  public async Task CompactAsync_SummarizerFactoryNull_FallsBackToServingModel()
  {
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 1500));
    conversation.AddAssistantMessage(new string('y', 1500));
    for (int i = 0; i < 6; i++)
    {
      conversation.AddUserMessage($"r{i}");
      conversation.AddAssistantMessage($"a{i}");
    }

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
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 1500));
    conversation.AddAssistantMessage(new string('y', 1500));
    for (int i = 0; i < 6; i++)
    {
      conversation.AddUserMessage($"r{i}");
      conversation.AddAssistantMessage($"a{i}");
    }

    RecordingFactory factory = new(null);
    ((ScriptedProvider)factory.Provider).Enqueue(Result.Success(new ModelResponse("   ", [])));
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("EmptySummary", outcome.Error.Code);
    Assert.Equal(14, conversation.Messages.Count); // untouched
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
}
