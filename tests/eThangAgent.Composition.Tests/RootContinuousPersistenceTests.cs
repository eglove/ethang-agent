using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Composition.Tests;

/// <summary>Spec D9: a root conversation persists every message at the safe point it is
///     added, exactly as a child run does, so a killed conversation process loses at most
///     the in-flight tool call - never the turn. The end-of-turn append must then never
///     re-append what the continuous sink already wrote: a doubled turn corrupts resume,
///     which is the exact failure this behaviour exists to prevent.</summary>
public sealed class RootContinuousPersistenceTests
{
  private sealed class ScriptedProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : Result.Success(new ModelResponse("done", [])));
  }

  private static ModelConfig Config => ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!;

  private static ModelConfig OtherConfig => ModelConfig.Create("other/model", null, 512, 0.5f, 8192).Value!;

  private static async Task<(TestAppDatabase Db, SqliteAgentStore Store, AgentId RootId)> SeedAsync()
  {
    TestAppDatabase db = TestAppDatabase.Create();
    SqliteAgentStore store = new(db.Database);
    AgentId rootId = AgentId.NewId();
    Result<string> saved = await store
        .SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, "ws", "openrouter"),
            TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(saved.IsSuccess);
    return (db, store, rootId);
  }

  private static async Task<IReadOnlyList<Message>> TranscriptAsync(SqliteAgentStore store, AgentId rootId)
  {
    Result<IReadOnlyList<Message>> transcript = await store
        .GetTranscriptAsync(rootId, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(transcript.IsSuccess);
    return transcript.Value;
  }

  /// <summary>Subscribes a sink to a conversation the way the agent does (the sink is an
  ///     ordinary <see cref="IChildTranscriptStore"/>; the agent's own wiring lives in
  ///     <see cref="AgentOptions.TranscriptSink"/>).</summary>
  private static IDisposable Attach(RootTranscriptSink sink, Conversation conversation)
      => conversation.Subscribe(message => _ = sink.AppendAsync(message),
          () => _ = sink.ReplaceAsync(conversation.Messages));

  [Fact]
  public async Task RootTurn_EachMessagePersistedAtSafePoint_TranscriptMatchesAfterEveryTurn()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => rootId);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);

      conversation.AddUserMessage("first question");
      conversation.AddAssistantMessage("first answer");

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["first question", "first answer"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootLifecycle_AppendExchangeAfterSinkWrite_DoesNotDuplicate()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => rootId);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);
      RootSessionLifecycle lifecycle = new(store, sink);

      conversation.AddUserMessage("hello");

      // The caller's count is STALE (0 - it was captured before the turn): a wired sink is
      // the authority on what it wrote, so the end-of-turn append skips the message the
      // continuous sink already persisted. Trusting the caller here would double the turn.
      await lifecycle.AppendExchangeAsync(rootId, conversation, 0,
          _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      _ = Assert.Single(transcript);
      Assert.Equal("hello", transcript[0].Content);
    }
  }

  [Fact]
  public async Task RootLifecycle_WithoutSink_StillAppendsTheWholeTurnSlice()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      RootSessionLifecycle lifecycle = new(store);
      conversation.AddUserMessage("legacy question");
      conversation.AddAssistantMessage("legacy answer");

      await lifecycle.AppendExchangeAsync(rootId, conversation, 0,
          _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["legacy question", "legacy answer"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootAgent_BuiltByTheHolder_PersistsEveryTurnMessageAtItsSafePoint()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => rootId);
#pragma warning restore CA2007
      ScriptedProvider provider = new(Result.Success(new ModelResponse("first answer", [])));
      RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]),
          dependencies: new RootDependencies(TranscriptSink: sink));
      Ag agent = holder.Build(existing: null, Config);

      Result<string> result = await agent
          .SendMessage("first question", ct: TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(result.IsSuccess);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["first question", "first answer"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootAgent_RebuiltForANewModel_DoesNotSubscribeTheSinkTwice()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => rootId);
#pragma warning restore CA2007
      ScriptedProvider provider = new();
      RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]),
          dependencies: new RootDependencies(TranscriptSink: sink));

      Ag first = holder.Build(existing: null, Config);
      conversation.AddUserMessage("before the rebuild");
      _ = holder.Build(first, OtherConfig);
      conversation.AddAssistantMessage("after the rebuild");

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["before the rebuild", "after the rebuild"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootSink_RootIdArrivingAfterConstruction_StaysInertUntilItIsKnown()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      // The composition fills RootSessionIdentity AFTER the container builds, so the sink
      // resolves its root lazily. Until the id exists the sink persists nothing and reports
      // zero - the end-of-turn append is then the whole record, never a duplicate.
      RootSessionIdentity identity = new();
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => identity.Id);
#pragma warning restore CA2007
      Assert.False(sink.IsActive);
      using IDisposable lease = Attach(sink, conversation);
      conversation.AddUserMessage("before the id");

      identity.Id = rootId;
      Assert.True(sink.IsActive);
      conversation.AddAssistantMessage("after the id");

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["after the id"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootSink_FlushedCount_ReportsWhatItWrote_SoTheAppendSkipsExactlyThose()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, () => rootId);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);

      conversation.AddUserMessage("one");
      conversation.AddAssistantMessage("two");

      Assert.Equal(2, await sink.FlushedCount().ConfigureAwait(true));
      Assert.Equal(0, sink.PendingCount);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["one", "two"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public void RootAgent_RebuiltForANewModel_RetiresThePreviousSubscription()
  {
    Conversation conversation = new();
    CountingSink counting = new();
    ScriptedProvider provider = new();
    RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]),
        dependencies: new RootDependencies(TranscriptSink: counting));

    Ag first = holder.Build(existing: null, Config);
    conversation.AddUserMessage("one");
    _ = holder.Build(first, OtherConfig);
    conversation.AddAssistantMessage("two");

    // The first build's subscription was retired, so each message reaches the sink ONCE.
    // Without the retirement the conversation carries two live root subscriptions and
    // every message is pushed twice - the doubled turn that corrupts resume.
    Assert.Equal(["one", "two"], counting.Appended.Select(m => m.Content));
  }

  /// <summary>Counts what reaches the sink, so a second live subscription is visible.</summary>
  private sealed class CountingSink : IChildTranscriptStore
  {
    public List<Message> Appended { get; } = [];

    public Task AppendAsync(Message message)
    {
      Appended.Add(message);
      return Task.CompletedTask;
    }

    public Task FlushAsync() => Task.CompletedTask;

    public Task<int> FlushedCount() => Task.FromResult(Appended.Count);

    public Task ReplaceAsync(IReadOnlyList<Message> messages) => Task.CompletedTask;
  }
}
