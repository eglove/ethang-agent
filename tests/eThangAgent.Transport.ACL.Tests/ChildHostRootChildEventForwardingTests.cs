using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ChildHost;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #281: child events published in the conversation HOST must reach the APP
///     process. The root turn - and therefore <c>agent.spawn</c> - runs in the root
///     container, so <c>ChildStartedEvent</c> is published onto THAT container's
///     <see cref="IAgentEvents"/>, and the root forwarder is the only one that can carry
///     it across the wire.
///     <para>WHY THIS FILE EXISTS: the sibling test
///     (<c>ChildHostRootEventForwardingTests</c>) asserts the host-side subscription through
///     the <c>AttachRootRun</c> rig - the seam tests drive - and
///     <c>RemoteAgentRuntimeE2ETests.ChildEventEnvelope_RoundTripsThroughTheWire</c>
///     hand-builds an envelope and proves the APP-side republish. Neither exercises the two
///     joined: the PRODUCTION build path (<c>ResolveRootHostForTest</c>) and arrival on the
///     app's own stream with NO steering message. When the forwarder was hung off the
///     root-steer path, both of those passed while every real session forwarded nothing -
///     so the defect was invisible to the suite. This is the assertion that was missing.</para>
///     <para>The rig is deliberately end-to-end: a real <see cref="ChildHostServer"/>,
///     a real named-pipe pair, and the real <see cref="RemoteAgentRuntime"/> pump. A test
///     that substituted any of them could pass while the production path stayed broken.</para></summary>
public class ChildHostRootChildEventForwardingTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  [Fact]
  public async Task RootContainerChildStarted_ReachesTheAppWithoutAnySteer()
  {
    HostRig rig = await HostRig.StartAsync().ConfigureAwait(true);
    try
    {
      // The PRODUCTION build path: the runner the host dispatches root turns to, resolved
      // exactly as HandleRootTurnAsync resolves it. Going through AttachRootRun instead
      // would test the seam rather than the wiring a real session uses.
      RootRunHost? runner = await rig.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(runner is not null, "the rig's settings name a root session, so a runner must build");
      IAgentEvents rootEvents = (IAgentEvents)runner.Services.GetRequiredService(typeof(IAgentEvents));

      AgentId childId = AgentId.NewId();
      // Exactly what SubAgentSpawner publishes when agent.spawn starts a run.
      rootEvents.Publish(new ChildStartedEvent(childId, DateTimeOffset.UtcNow,
          new AgentId(rig.RootId), "probe/model", 0));

      // No steering message is sent at any point in this test - that is the whole point.
      ChildEvent? delivered = await rig.WaitForEventAsync(childId).ConfigureAwait(true);

      ChildStartedEvent started = Assert.IsType<ChildStartedEvent>(delivered);
      Assert.Equal(childId, started.ChildId);
      Assert.Equal("probe/model", started.ModelId, StringComparer.Ordinal);
    }
    finally
    {
      await rig.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootContainerSettled_ReachesTheAppWithoutAnySteer()
  {
    // The settle is the other half of what the Progress view needs: a row that becomes a
    // row must also stop being 'running', or the panel shows finished work forever.
    HostRig rig = await HostRig.StartAsync().ConfigureAwait(true);
    try
    {
      RootRunHost? runner = await rig.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(runner is not null);
      IAgentEvents rootEvents = (IAgentEvents)runner.Services.GetRequiredService(typeof(IAgentEvents));

      AgentId childId = AgentId.NewId();
      rootEvents.Publish(new ChildSettledEvent(childId, DateTimeOffset.UtcNow,
          AgentStatus.Completed, null, 12));

      ChildEvent? delivered = await rig.WaitForEventAsync(childId).ConfigureAwait(true);

      ChildSettledEvent settled = Assert.IsType<ChildSettledEvent>(delivered);
      Assert.Equal(childId, settled.ChildId);
      Assert.Equal(AgentStatus.Completed, settled.TerminalStatus);
    }
    finally
    {
      await rig.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>WHY THE ROOT FORWARDER IS THE ONLY ONE THAT MATTERS (the issue's third
  ///     question, answered as an assertion so it cannot quietly stop being true).
  ///     <para>A child runs inside the HOST process, in the HOST's own container, so
  ///     <c>SubAgentSpawner</c> publishes <c>ChildStartedEvent</c> onto that container's
  ///     stream - the same object the root forwarder subscribes. The per-child forwarder
  ///     in <c>RunChildAsync</c> belongs to the wire <c>StartCommand</c> path, which the
  ///     app never exercises: it addresses children through the WIRE runtime, whose
  ///     <c>Start</c> sends a start envelope, but the host's own container resolves the
  ///     IN-PROCESS runtime and runs the child directly.</para>
  ///     <para>If this assertion ever fails, the host was wired to a remote runtime and the
  ///     root forwarder alone would stop being sufficient - which is exactly the change
  ///     someone needs to notice rather than discover from an empty Progress view.</para></summary>
  [Fact]
  public async Task HostContainer_RunsItsChildrenInProcess_SoTheRootForwarderCarriesThem()
  {
    HostRig rig = await HostRig.StartAsync().ConfigureAwait(true);
    try
    {
      RootRunHost? runner = await rig.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(runner is not null);
      object runtime = runner.Services.GetRequiredService(typeof(IAgentRuntime));
      _ = Assert.IsType<InProcessAgentRuntime>(runtime);
    }
    finally
    {
      await rig.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>One real host talking to one real app over a real named pipe. Everything
  ///     between the root container's stream and the app's stream is production code.</summary>
  private sealed class HostRig : IAsyncDisposable
  {
    private readonly string _directory;
    private readonly NamedPipeChildTransport _app;
    private readonly NamedPipeChildTransport _hostSide;
    private readonly CancellationTokenSource _loop = new();

    private HostRig(string directory, ChildHostServer server, Guid rootId,
        NamedPipeChildTransport app, NamedPipeChildTransport hostSide,
        RecordingEvents events)
    {
      _directory = directory;
      Server = server;
      RootId = rootId;
      _app = app;
      _hostSide = hostSide;
      Events = events;
    }

    public ChildHostServer Server { get; }

    public Guid RootId { get; }

    /// <summary>The APP-side stream the Desktop's Progress view and badge subscribe.</summary>
    public RecordingEvents Events { get; }

    public static async Task<HostRig> StartAsync()
    {
      string directory = Directory.CreateTempSubdirectory("ethang-281-").FullName;
      Guid rootId = Guid.NewGuid();
      string settings = Path.Combine(directory, "settings.json");
      // The host builds a REAL container, so the provider needs a key to resolve. The
      // base URL is unroutable on purpose: nothing in this test may reach the network.
      await File.WriteAllTextAsync(settings,
          "{\"OpenRouter\":{\"ApiKey\":\"sk-or-probe\","
          + "\"BaseUrl\":\"https://openrouter.invalid/api/v1\"},\"SubAgents\":{},"
          + "\"WorkspaceRoot\":" + System.Text.Json.JsonSerializer.Serialize(directory) + ","
          + "\"SessionId\":" + System.Text.Json.JsonSerializer.Serialize(rootId.ToString()) + "}",
          TestContext.Current.CancellationToken).ConfigureAwait(true);

      ChildHostServer server = new(settings, Path.Combine(directory, "db.sqlite"));
      string pipeName = "ethang-281-" + Guid.NewGuid().ToString("N");
      Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
          TestContext.Current.CancellationToken);
      NamedPipeChildTransport app = await NamedPipeChildTransport
          .ConnectToHostAsync(pipeName, TestContext.Current.CancellationToken)
          .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      NamedPipeChildTransport hostSide = await accept
          .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      server.AttachTransport(hostSide);
      _ = server.ServeAsync();

      RecordingEvents events = new();
      RemoteAgentRuntime runtime = new(app, RootTurnBudget.Default, events);
      HostRig rig = new(directory, server, rootId, app, hostSide, events);
      _ = runtime.RunReceiveLoopAsync(rig._loop.Token);
      return rig;
    }

    /// <summary>Waits for the app-side stream to see ANY event for the child. Bounded: a
    ///     forwarder that never attached would otherwise hang the suite instead of failing
    ///     it (deadlock vigilance).</summary>
    public async Task<ChildEvent?> WaitForEventAsync(AgentId childId)
    {
      DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
      while (DateTimeOffset.UtcNow < deadline)
      {
        if (Events.Published.Find(e => e.ChildId == childId) is { } found)
        {
          return found;
        }

        await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      return Events.Published.Find(e => e.ChildId == childId);
    }

    public async ValueTask DisposeAsync()
    {
      await _loop.CancelAsync().ConfigureAwait(false);
      await _app.DisposeAsync().ConfigureAwait(false);
      await _hostSide.DisposeAsync().ConfigureAwait(false);
      _loop.Dispose();
      try
      {
        Directory.Delete(_directory, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a temp directory the OS will reclaim
      }
    }
  }

  /// <summary>The app-side stream: records what the runtime republished. The production
  ///     app wires <c>InProcessAgentEvents</c>; recording is the point of a test double
  ///     here, and the publish/subscribe contract is identical.</summary>
  private sealed class RecordingEvents : IAgentEvents
  {
    private readonly List<ChildEvent> _published = [];

    public List<ChildEvent> Published
    {
      get
      {
        lock (_published)
        {
          return [.. _published];
        }
      }
    }

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      ArgumentNullException.ThrowIfNull(subscriber);
      return new NoopLease();
    }

    public void Publish(ChildEvent evt)
    {
      ArgumentNullException.ThrowIfNull(evt);
      lock (_published)
      {
        _published.Add(evt);
      }
    }

    private sealed class NoopLease : IDisposable
    {
      public void Dispose()
      {
      }
    }
  }
}
