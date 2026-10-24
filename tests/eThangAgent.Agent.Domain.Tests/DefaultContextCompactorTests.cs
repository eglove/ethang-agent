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

  /// <summary>THE EMPTY-SUMMARY GUARD HOLDS FROM THE HANDOFF SIDE TOO (issue #234):
  ///     even with handoff ENABLED, an empty summarizer output is a FAILURE outcome —
  ///     never a handoff. The guard lives in SummarizeAsync, which runs BEFORE the
  ///     handoff decision, so the invariant does not rest on statement order alone.</summary>
  [Fact]
  public async Task CompactAsync_HandoffEnabled_EmptySummary_Fails_NeverHandsOff()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    ((ScriptedProvider)factory.Provider).Enqueue(Result.Success(new ModelResponse("   ", [])));
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(true));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("EmptySummary", outcome.Error.Code); // a FAILURE outcome, not a handoff
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

  // ---- handoff replaces in-place compaction (issue #234 task 5) ----

  [Fact]
  public void CompactionOutcome_HandoffFactory_CarriesSummaryAndIsHandoff()
  {
    CompactionOutcome handoff = CompactionOutcome.Handoff("the summary", new TokenUsage(50, 100));

    Assert.True(handoff.IsHandoff);
    Assert.Equal("the summary", handoff.HandoffSummary);
    Assert.Equal(new TokenUsage(50, 100), handoff.SummarizerUsage);
  }

  [Fact]
  public void CompactionOutcome_Default_IsNotHandoff()
  {
    CompactionOutcome ordinary = new(3, 12, null);

    Assert.False(ordinary.IsHandoff);
    Assert.Null(ordinary.HandoffSummary);
  }

  /// <summary>At the root (no ambient running child) with handoff enabled, the eviction
  /// path's summary becomes a HANDOFF: the conversation is NOT compacted in place - the
  /// old transcript stays intact - and the outcome carries the summary for the new
  /// session's seed.</summary>
  [Fact]
  public async Task CompactAsync_HandoffEnabled_AtRoot_SkipsCompactAndReturnsHandoffOutcome()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    int countBefore = conversation.Messages.Count;

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(true));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.True(outcome.Value.IsHandoff);
    Assert.Equal("summary text", outcome.Value.HandoffSummary);
    Assert.Equal(countBefore, conversation.Messages.Count); // NOT compacted in place
  }

  /// <summary>With handoff disabled the compaction stays in place: the conversation is
  /// compacted and the outcome is an ordinary (non-handoff) one.</summary>
  [Fact]
  public async Task CompactAsync_HandoffDisabled_CompactsInPlace()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(false));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(outcome.Value.IsHandoff);
    Assert.Null(outcome.Value.HandoffSummary);
    Assert.True(conversation.Messages[0].IsSummary); // the in-place summary message landed
  }

  /// <summary>With no enablement delegate at all (legacy wiring) the compaction stays
  /// in place byte-identically.</summary>
  [Fact]
  public async Task CompactAsync_NoHandoffDelegate_CompactsInPlace()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(outcome.Value.IsHandoff);
    Assert.True(conversation.Messages[0].IsSummary);
  }

  /// <summary>The tail-compression fallback NEVER hands off (it produces no summary),
  /// even when the handoff delegate is enabled.</summary>
  [Fact]
  public async Task CompactAsync_TailCompressionFallback_NeverHandsOff()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("task");
    for (int i = 0; i < 3; i++)
    {
      conversation.AddUserMessage($"kept user {i}");
      conversation.AddAssistantMessage("", [new ToolCall($"c{i}", "read", "{}")]);
      conversation.AddToolResult($"c{i}", new string('r', 800));
    }

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(true));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(outcome.Value.IsHandoff);
  }

  /// <summary>OVERFLOW RECOVERY IS ALWAYS IN PLACE: forceInPlace bypasses the handoff
  /// decision even when the delegate is wired and true — the ceiling path is the only
  /// path that may decide a handoff.</summary>
  [Fact]
  public async Task CompactAsync_ForceInPlace_BypassesTheHandoffDecision()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(true));

    Result<CompactionOutcome> outcome = await compactor.CompactInPlaceAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(outcome.Value.IsHandoff);
    Assert.True(conversation.Messages[0].IsSummary); // compacted in place
  }

  /// <summary>An enablement delegate that FAILS (a misconfigured preference read) is a
  /// typed failure outcome — the compactor never guesses enabled/disabled, and the
  /// existing compaction-failure notice path carries the error.</summary>
  [Fact]
  public async Task CompactAsync_EnablementFailure_IsATypedFailure()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    int countBefore = conversation.Messages.Count;

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null,
        handoffEnabled: () => Result.Failure<bool>(new DomainError("InvalidPreferenceValue",
            "compaction_handoff_enabled must be 'true' or 'false', got 'yes'.")));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.False(outcome.IsSuccess);
    Assert.Equal("InvalidPreferenceValue", outcome.Error.Code);
    Assert.Equal(countBefore, conversation.Messages.Count); // untouched
  }

  /// <summary>ROOT-GATING: with a child ambient (a running child on this async flow) the
  /// compaction stays in place even when the handoff delegate is enabled - a child has
  /// no session to hand off.</summary>
  [Fact]
  public async Task CompactAsync_HandoffEnabled_WithRunningChildAmbient_CompactsInPlace()
  {
    Conversation conversation = ConversationWithEvictableMiddle();

    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null, handoffEnabled: () => Result.Success(true));
    AgentRecord child = AgentRecord.Spawned(
        AgentId.NewId(), new AgentId(Guid.NewGuid()), 1, "m/sub", "child", "task",
        DateTimeOffset.UtcNow);
    using IDisposable ambient = SubAgentSpawner.PushRunningChildForTests(child);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.False(outcome.Value.IsHandoff);
    Assert.True(conversation.Messages[0].IsSummary); // compacted in place
  }

  // ---- summarizer identity for the usage ledger (issue #23) ----

  /// <summary>The outcome names the model that ACTUALLY wrote the summary: the resolved
  ///     writer when the summarizer resolver supplies one.</summary>
  [Fact]
  public async Task CompactAsync_SummarizerResolved_OutcomeNamesTheSummarizerModel()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    ModelConfig summarizer = ModelConfig.Create("summarizer-model", "openrouter", 100, 0.5f, 1000).Value!;
    RecordingFactory factory = new(summarizer);
    DefaultContextCompactor compactor = new(factory, () => summarizer);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.Equal("summarizer-model", outcome.Value.SummarizerModelId);
    Assert.Equal("openrouter", outcome.Value.SummarizerProvider);
  }

  /// <summary>On FALLBACK the writer IS the serving model, and the outcome must say so:
  ///     the price stamped onto the usage row is the price of the model that ran.</summary>
  [Fact]
  public async Task CompactAsync_SummarizerFallsBack_OutcomeNamesTheServingModel()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    RecordingFactory factory = new(null);
    DefaultContextCompactor compactor = new(factory, () => null);

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.Equal("serving", outcome.Value.SummarizerModelId);
    Assert.Null(outcome.Value.SummarizerProvider);
  }

  /// <summary>A HANDOFF outcome carries the summarizer identity too (issue #23): the
  ///     summarizer was called and billed whether or not the conversation was compacted.</summary>
  [Fact]
  public async Task CompactAsync_HandoffOutcome_CarriesTheSummarizerIdentity()
  {
    Conversation conversation = ConversationWithEvictableMiddle();
    ModelConfig summarizer = ModelConfig.Create("summarizer-model", "openrouter", 100, 0.5f, 1000).Value!;
    RecordingFactory factory = new(summarizer);
    DefaultContextCompactor compactor = new(factory, () => summarizer, handoffEnabled: () => Result.Success(true));

    Result<CompactionOutcome> outcome = await compactor.CompactAsync(
        conversation, Serving, TestContext.Current.CancellationToken);

    Assert.True(outcome.IsSuccess, outcome.Error?.Code + ": " + outcome.Error?.Message);
    Assert.True(outcome.Value.IsHandoff);
    Assert.Equal("summarizer-model", outcome.Value.SummarizerModelId);
    Assert.Equal("openrouter", outcome.Value.SummarizerProvider);
  }

  /// <summary>The tail-compression fallback makes NO summarizer call, so its outcome names
  ///     no model: a usage row must never be attributed to a model that did not run.</summary>
  [Fact]
  public async Task CompactAsync_TailCompressionFallback_OutcomeNamesNoSummarizerModel()
  {
    // Tail-dominated bloat with oversized tool results: the plan evicts nothing, so the
    // deep clean compresses and makes NO summarizer call.
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
    Assert.Null(outcome.Value.SummarizerUsage);
    Assert.Null(outcome.Value.SummarizerModelId);
  }
}
