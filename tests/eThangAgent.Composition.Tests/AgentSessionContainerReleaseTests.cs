using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #388: a closed session must release what it was wired to.
///     <para>The host UI attaches a notice sink that captures its session view-model and its tab, so
///     the container's <see cref="SessionNoticeSink"/> carrier is an edge from the container back
///     into the whole shell. Nothing released it: 17 of 19 containers in a process running 2 agents
///     were disposed yet still reachable, each holding its daemon host, exec engine, watcher lease
///     and the entire UI graph behind that closure.</para>
///     <para>The plain tests build their own two-service container, so the fast tier runs them; the
///     composed one drives the real factory, which is what makes it a process test.</para></summary>
public class AgentSessionContainerReleaseTests
{
  /// <summary>The shape the leak had: something outside the session still holds a service it
  ///     RESOLVED from the container - a pending timer's captured scope, a caller that never
  ///     finished - so the container cannot be collected. What must not follow from that is the
  ///     shell: the sink closure reaches the view-model, and while the carrier still held it the
  ///     whole surface behind a closed session's container stayed reachable.
  ///     <para>The falsifying check is direct: without releasing the sink on disposal, the
  ///     captured surface survives every collection because the carrier is deliberately kept
  ///     alive here.</para></summary>
  [Fact]
  public void A_Resolved_Service_Does_Not_Keep_The_Closed_Sessions_Surface_Alive()
  {
    (WeakReference surface, SessionNoticeSink carrier) = AttachDisposeAndDrop();

    // The carrier is the resolved service an outside holder keeps: it stands in for every
    // container-internal subscriber that outlives the disposal.
    Assert.False(carrier.IsAttached, "a disposed session's carrier names no shell");
    AssertCollected(surface, "the surface the closed session's sink captured");
  }

  [Fact]
  public void Disposing_Releases_The_Container_And_What_It_Captured()
  {
    // Built in its own frame: a strong local outlives the collection in a debug build, so the
    // references under test must not be reachable from this method's own locals.
    (WeakReference container, WeakReference captured) = AttachDisposeAndDropContainer();

    AssertCollected(container, "the disposed container");
    AssertCollected(captured, "the surface the sink captured");
  }

  [Fact]
  public async Task Disposing_Clears_The_Sink_While_The_Session_Still_Exists()
  {
    (AgentSession session, SessionNoticeSink carrier, ServiceProvider provider) = BuildSession();
    object captured = new();
    session.NoticeSink = message => GC.KeepAlive(captured);
    Assert.True(carrier.IsAttached, "attaching a sink fills the container's carrier");

    Assert.True(await session.TryDisposeContainerAsync());

    Assert.Null(session.NoticeSink);
    Assert.False(carrier.IsAttached, "the container must not keep reaching into the shell it served");
    Assert.False(await session.TryDisposeContainerAsync(), "a second disposal is a no-op");
    GC.KeepAlive(provider);
  }

  /// <summary>Builds a session, attaches a sink in the shell's own shape - a closure reaching the
  ///     surface behind it - disposes it, and returns probes for the container and the captured
  ///     surface. The session is dropped with this frame, so nothing here roots either probe.
  ///     </summary>
  private static (WeakReference Container, WeakReference Captured) AttachDisposeAndDropContainer()
  {
    (AgentSession session, SessionNoticeSink carrier, ServiceProvider provider) = BuildSession();
    WeakReference container = new(provider);
    Surface captured = new();
    session.NoticeSink = message => captured.Deliver(message);
    carrier.Post("one notice reaches the surface");
    Assert.Equal(1, captured.Delivered);

    Assert.True(session.TryDisposeContainerAsync().GetAwaiter().GetResult());
    Assert.False(carrier.IsAttached);

    return (container, new WeakReference(captured));
  }

  /// <summary>Attaches a sink, disposes, and drops the session and the container - returning the
  ///     carrier and a probe for the captured surface. Nothing here roots the surface.</summary>
  private static (WeakReference Surface, SessionNoticeSink Carrier) AttachDisposeAndDrop()
  {
    (AgentSession session, SessionNoticeSink carrier, ServiceProvider provider) = BuildSession();
    Surface captured = new();
    session.NoticeSink = message => captured.Deliver(message);
    Assert.True(carrier.IsAttached);

    Assert.True(session.TryDisposeContainerAsync().GetAwaiter().GetResult());
    GC.KeepAlive(provider);
    return (new WeakReference(captured), carrier);
  }

  /// <summary>Stands in for the shell's session view-model: the object the sink closure captures.
  ///     </summary>
  private sealed class Surface
  {
    public int Delivered { get; private set; }

    public void Deliver(string message)
    {
      ArgumentNullException.ThrowIfNull(message);
      Delivered++;
    }
  }

  [Fact]
  public void A_Built_Container_Starts_Unattached_So_Notices_Are_Dropped()
  {
    (AgentSession session, SessionNoticeSink carrier, ServiceProvider provider) = BuildSession();
    try
    {
      Assert.False(carrier.IsAttached);
      carrier.Post("nobody is listening"); // by contract, not by throwing
      Assert.Empty(session.PendingNotices);
    }
    finally
    {
      GC.KeepAlive(provider);
    }
  }

  /// <summary>THE WHOLE CHAIN, through the composed container a production session builds: the
  ///     host attaches its sink exactly as the shell does, the container is disposed, and the
  ///     surface behind the closure must go. This is the arithmetic the issue measured - 19
  ///     containers for 2 live agents, each one retaining the view-model its sink captured.
  ///     <para>The watcher is started first, because a composition that never touched the
  ///     language server would trivially pass: the retention lived in the running watcher's
  ///     subscriber list.</para>
  ///     <para>WHAT THIS DOES NOT ASSERT: the disposed CONTAINER itself is still reachable in this
  ///     scenario, through a composition-internal edge the sink and the watcher are not on. That
  ///     retention is a third root the issue named and this change does not close, so it is not
  ///     asserted here - a test that folded it in would report the shell edge as unproven.</para>
  ///     </summary>
  [Trait("Category", "Process")]
  [Fact]
  public void A_Closed_Composed_Session_Releases_The_Surface_Its_Sink_Captured()
  {
    (WeakReference surface, _) = ComposeAttachCloseAndDrop();

    AssertCollected(surface, "the surface the closed session's sink captured");
  }

  /// <summary>THE GROWTH SHAPE the issue reported - linear in every agent ever started, not in
  ///     conversation length - measured as a count: several composed sessions are opened and
  ///     closed in turn, and the surface behind each one's sink must not accumulate.
  ///     <para>The CONTAINER is not asserted here. A disposed container stays reachable through a
  ///     composition-internal edge this change does not close, so a count over containers would
  ///     measure that edge rather than the sink. What is asserted is the edge that welded a closed
  ///     session to the shell: the surface its sink captured, once per cycle, every one freed.
  ///     </para></summary>
  [Trait("Category", "Process")]
  [Fact]
  public void Opening_And_Closing_Sessions_Does_Not_Accumulate_The_Shell_Behind_Them()
  {
    const int Cycles = 3;
    List<WeakReference> surfaces = [];
    for (int i = 0; i < Cycles; i++)
    {
      (WeakReference surface, _) = ComposeAttachCloseAndDrop();
      surfaces.Add(surface);
    }

    Assert.Equal(0, LiveAfterCollection(surfaces));
  }

  /// <summary>One composed session, its sink attached in the shell's shape, its watcher started,
  ///     then closed and dropped with this frame.</summary>
  private static (WeakReference Surface, WeakReference Carrier) ComposeAttachCloseAndDrop()
  {
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("session-release-composed");
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSession session = BuildComposedSession(db, workspace.FullName);
    SessionNoticeSink carrier = session.Services.GetRequiredService<SessionNoticeSink>();
    // The hold's subscriber list is where a closed session's view-model used to stay reachable.
    _ = session.WorkspaceWatcher?.Subscribe(_ => Task.CompletedTask);

    Surface surface = new();
    session.NoticeSink = message => surface.Deliver(message);
    carrier.Post("one notice reaches the surface");
    Assert.Equal(1, surface.Delivered);

    Assert.True(session.TryDisposeContainerAsync().GetAwaiter().GetResult());
    Assert.False(carrier.IsAttached);
    db.Dispose();
    Directory.Delete(workspace.FullName, true);
    return (new WeakReference(surface), new WeakReference(carrier));
  }

  /// <summary>A session built by the factory over the real composition, which is the shape a
  ///     production session is: a full container, its own watcher hold, and its own carrier.
  ///     </summary>
  private static AgentSession BuildComposedSession(TestAppDatabase db, string workspaceRoot)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    AgentSessionFactory factory = new(settings, db.Database);
    Result<AgentSession> opened = factory
        .CreateAsync(workspaceRoot, Providers.OpenRouter, conversationSeed: null)
        .GetAwaiter().GetResult();
    Assert.True(opened.IsSuccess, opened.IsSuccess ? string.Empty : opened.Error.Message);
    return opened.Value;
  }

  /// <summary>A session over a container that resolves the notice carrier, the way the composed
  ///     container does. Nothing else in the graph matters to the edge under test.</summary>
  private static (AgentSession Session, SessionNoticeSink Carrier, ServiceProvider Provider) BuildSession()
  {
    ServiceCollection services = new();
    _ = services.AddSingleton<SessionNoticeSink>();
    ServiceProvider provider = services.BuildServiceProvider();
    AgentSession session = new(
        provider,
        AgentId.NewId(),
        new Conversation(),
        Handler: null!,
        Lifecycle: new RootSessionLifecycle(new StubLifecycleStore()),
        Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
        WorkspaceRoot: TestTempRoot.CreateDirectory("session-release").FullName,
        ProviderName: "openrouter",
        Inbox: new BoundedAgentMailbox(),
        ChildRuntime: new NoopRuntime());
    return (session, provider.GetRequiredService<SessionNoticeSink>(), provider);
  }

  /// <summary>How many of the referenced objects survive a forced reclaim, collected in rounds
  ///     because unload and finalization are asynchronous.</summary>
  private static int LiveAfterCollection(List<WeakReference> targets)
  {
    int alive = targets.Count;
    for (int attempt = 0; attempt < 12; attempt++)
    {
#pragma warning disable S1215 // Do not force garbage collection
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      GC.WaitForPendingFinalizers();
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore S1215 // Do not force garbage collection
      alive = targets.Count(t => t.IsAlive);
      if (alive == 0)
      {
        return 0;
      }
    }

    return alive;
  }

  private static void AssertCollected(WeakReference target, string what)
  {
    for (int attempt = 0; attempt < 12; attempt++)
    {
#pragma warning disable S1215 // Do not force garbage collection
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
      GC.WaitForPendingFinalizers();
      GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore S1215 // Do not force garbage collection
      if (!target.IsAlive)
      {
        return;
      }
    }

    Assert.Fail($"{what} is still reachable after collection");
  }

  private sealed class NoopRuntime : IAgentRuntime
  {
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not found")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentId>(new DomainError("ResumeUnsupported", "not exercised.")));

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? agentId = null) { }
  }

  private sealed class StubLifecycleStore : IAgentStore
  {
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "not found")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
