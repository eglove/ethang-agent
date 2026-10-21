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
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
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
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);
      RootSessionLifecycle lifecycle = new(store, sink);

      conversation.AddUserMessage("hello");

      // The caller's count is STALE (0 - it was captured before the turn): a wired sink is
      // the authority on what it wrote, so the end-of-turn append skips the message the
      // continuous sink already persisted. Trusting the caller here would double the turn.
      await lifecycle.AppendExchangeAsync(rootId, conversation, 0, "final",
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

      await lifecycle.AppendExchangeAsync(rootId, conversation, 0, "final",
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
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
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
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
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
      await using RootTranscriptSink sink = new(store, conversation, () => identity.Id);
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
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
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
    // Named decision (CA2000): the sink is asserted on, never disposed - the test ends
    // before any flush could matter, and its store records synchronously.
    RecordingStore store = new();
#pragma warning disable CA2000 // Dispose objects before losing scope
    RootTranscriptSink sink = new(store, conversation, () => AgentId.NewId());
#pragma warning restore CA2000
    ScriptedProvider provider = new();
    RootAgentHolder holder = new(provider, conversation, new ToolRegistry([]),
        dependencies: new RootDependencies(TranscriptSink: sink));

    Ag first = holder.Build(existing: null, Config);
    conversation.AddUserMessage("one");
    _ = holder.Build(first, OtherConfig);
    conversation.AddAssistantMessage("two");

    // The first build's subscription was retired: the conversation carries ONE live root
    // subscription, not one per build. Without the retirement every later message would be
    // pushed once per build - the doubled turn that corrupts resume. The store assertion is
    // kept because the sink's reference dedupe is a second line of defence; the retirement
    // counter is what pins the retirement itself (dedupe would hide a missed one).
    Assert.Equal(1, sink.Retirements);
    Assert.Equal(["one", "two"], store.Appended.Select(m => m.Content));
  }

  [Fact]
  public void RootSink_TrackSubscription_DisposesThePreviousLease()
  {
    // The retirement must actually DISPOSE the prior lease, not merely replace the field:
    // a live first subscription keeps pushing every message a second time.
    Conversation conversation = new();
    // Named decision (CA2000): the sink is asserted on, never disposed here.
#pragma warning disable CA2000 // Dispose objects before losing scope
    RootTranscriptSink sink = new(new RecordingStore(), conversation, () => AgentId.NewId());
#pragma warning restore CA2000
    // Named decision (CA2000): the leases ARE the assertion subject - the sink disposes the
    // first, and the second is deliberately left live by the test's end.
#pragma warning disable CA2000 // Dispose objects before losing scope
    RecordingLease first = new();
    RecordingLease second = new();
#pragma warning restore CA2000

    sink.TrackSubscription(first);
    Assert.False(first.Disposed);

    sink.TrackSubscription(second);
    Assert.True(first.Disposed);
    Assert.False(second.Disposed);
    Assert.Equal(1, sink.Retirements);
  }

  /// <summary>A subscription lease that records its own disposal.</summary>
  private sealed class RecordingLease : IDisposable
  {
    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
  }

  /// <summary>Records what reaches the store, so a second live subscription is visible as a
  ///     duplicated append.</summary>
  private sealed class RecordingStore : IAgentStore
  {
    public List<Message> Appended { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no records")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
      lock (Appended)
      {
        Appended.Add(message);
      }

      return Task.FromResult(Result.Success(id.ToString()));
    }

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }


  /// <summary>A store whose appends ALWAYS fail: the drain must leave the message pending and
  ///     RETURN, never spin (a locked or full database degrades instead of wedging).</summary>
  private sealed class FailingStore : IAgentStore
  {
    public int Attempts { get; private set; }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id.ToString()));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no records")));

    // YIELDS: a synchronously-completing failure lets the retry loop run without ever
    // suspending, which would block the CALLER instead of letting it observe the spin.
    public async Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
      Attempts++;
      await Task.Yield();
      return Result.Failure<string>(new DomainError("DbLocked", "database is locked"));
    }

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("DbLocked", "database is locked")));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }

  // ── Resume: the sink's count is RELATIVE, the conversation index is ABSOLUTE ──────────

  [Fact]
  public async Task RootLifecycle_ResumedConversation_DoesNotReappendTheHydratedPrefix()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      // RESUME: the conversation is hydrated with the messages a prior session persisted,
      // and those rows are already in the store. The sink starts its own count at ZERO, so
      // treating that count as a conversation index re-appends the whole turn.
      Message[] hydrated =
      [
        new(Role.User, "old question", DateTimeOffset.UtcNow),
        new(Role.Assistant, "old answer", DateTimeOffset.UtcNow),
      ];
      Conversation conversation = new(hydrated);
      foreach (Message message in hydrated)
      {
        _ = await store.AppendMessageAsync(rootId, message, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
      }

      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, conversation, () => rootId);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);
      RootSessionLifecycle lifecycle = new(store, sink);

      int before = conversation.Messages.Count;
      conversation.AddUserMessage("new question");
      conversation.AddAssistantMessage("new answer");
      await lifecycle.AppendExchangeAsync(rootId, conversation, before, "final",
        _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["old question", "old answer", "new question", "new answer"],
          transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootLifecycle_InertSink_PersistsEveryMessageExactlyOnceAcrossTurns()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      // A wired-but-INERT sink (no root id yet): it writes nothing and its count stays 0,
      // so the caller's conversation-derived count must remain the baseline.
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, conversation, () => null);
#pragma warning restore CA2007
      using IDisposable lease = Attach(sink, conversation);
      RootSessionLifecycle lifecycle = new(store, sink);
      Assert.False(sink.IsActive);

      int beforeFirst = conversation.Messages.Count;
      conversation.AddUserMessage("q1");
      conversation.AddAssistantMessage("a1");
      await lifecycle.AppendExchangeAsync(rootId, conversation, beforeFirst, "final",
        _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      int beforeSecond = conversation.Messages.Count;
      conversation.AddUserMessage("q2");
      conversation.AddAssistantMessage("a2");
      await lifecycle.AppendExchangeAsync(rootId, conversation, beforeSecond, "final",
        _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["q1", "a1", "q2", "a2"], transcript.Select(m => m.Content));
    }
  }

  [Fact]
  public async Task RootLifecycle_ReplaceTranscriptWithInertSink_WritesTheReplacement()
  {
    (TestAppDatabase db, SqliteAgentStore store, AgentId rootId) = await SeedAsync().ConfigureAwait(true);
    using (db)
    {
      // A compacted/shrunk turn replaces the transcript. An inert sink writes nothing, so
      // the lifecycle must do the replacement itself — otherwise the turn persists NOTHING.
      Conversation conversation = new();
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using RootTranscriptSink sink = new(store, conversation, () => null);
#pragma warning restore CA2007
      RootSessionLifecycle lifecycle = new(store, sink);
      conversation.AddUserMessage("kept question");
      conversation.AddAssistantMessage("kept answer");

      await lifecycle.ReplaceTranscriptAsync(rootId, conversation,
          _ => Assert.Fail("no errors expected")).ConfigureAwait(true);

      IReadOnlyList<Message> transcript = await TranscriptAsync(store, rootId).ConfigureAwait(true);

      Assert.Equal(["kept question", "kept answer"], transcript.Select(m => m.Content));
    }
  }

  // ── A failing store must degrade, never wedge ────────────────────────────────────────

  [Fact]
  public async Task RootSink_FlushWhenTheStoreKeepsFailing_ReturnsInsteadOfSpinning()
  {
    FailingStore failing = new();
    // Named decision (CA2000): NOT disposed on purpose — a spinning drain also wedges
    // Dispose, and the point of this test is that no caller may be wedged.
#pragma warning disable CA2000 // Dispose objects before losing scope
    RootTranscriptSink sink = new(failing, new Conversation(), () => AgentId.NewId());
#pragma warning restore CA2000

    // The drain must give up after one failed attempt and leave the message pending; an
    // unbounded retry hangs AppendAsync, FlushedCount, and Dispose alike.
    Task append = sink.AppendAsync(new Message(Role.User, "stuck", DateTimeOffset.UtcNow));
    Task completed = await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken)).ConfigureAwait(true);

    Assert.Same(append, completed);
    Assert.True(sink.HasPending, "the failed message must stay queued for the next attempt");
    Assert.Equal(1, failing.Attempts);
  }
}
