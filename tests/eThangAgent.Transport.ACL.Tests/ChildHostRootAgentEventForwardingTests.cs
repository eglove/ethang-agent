using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ChildHost;
using eThangAgent.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #281: agent events published in the conversation HOST must reach the APP
///     process. The root turn - and therefore <c>agent.spawn</c> - runs in the root
///     container, so <c>AgentStartedEvent</c> is published onto THAT container's
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

      AgentId agentId = AgentId.NewId();
      // Exactly what SubAgentSpawner publishes when agent.spawn starts a run.
      rootEvents.Publish(new AgentStartedEvent(agentId, DateTimeOffset.UtcNow,
          new AgentId(rig.RootId), "probe/model", 0));

      // No steering message is sent at any point in this test - that is the whole point.
      AgentEvent? delivered = await rig.WaitForEventAsync(agentId).ConfigureAwait(true);

      AgentStartedEvent started = Assert.IsType<AgentStartedEvent>(delivered);
      Assert.Equal(agentId, started.Id);
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
    // The settle is the other half of the lifecycle fact: an agent that started must also be
    // reported as no longer running, or the app shows finished work as live forever.
    HostRig rig = await HostRig.StartAsync().ConfigureAwait(true);
    try
    {
      RootRunHost? runner = await rig.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(runner is not null);
      IAgentEvents rootEvents = (IAgentEvents)runner.Services.GetRequiredService(typeof(IAgentEvents));

      AgentId agentId = AgentId.NewId();
      rootEvents.Publish(new AgentSettledEvent(agentId, DateTimeOffset.UtcNow,
          AgentStatus.Completed, null, 12));

      AgentEvent? delivered = await rig.WaitForEventAsync(agentId).ConfigureAwait(true);

      AgentSettledEvent settled = Assert.IsType<AgentSettledEvent>(delivered);
      Assert.Equal(agentId, settled.Id);
      Assert.Equal(AgentStatus.Completed, settled.TerminalStatus);
    }
    finally
    {
      await rig.DisposeAsync().ConfigureAwait(true);
    }
  }

  /// <summary>THE HOST'S ORCHESTRATION SEAM LAUNCHES A HOST (issue #330 review F3), and this
  ///     assertion replaces the one that used to pin the opposite.
  ///     <para>WHAT IT USED TO SAY, AND WHY THAT WAS WRONG: it asserted the root container's
  ///     <see cref="IAgentRuntime"/> was the IN-PROCESS runner, reasoning that a child therefore
  ///     runs in the host's own container and the root forwarder alone carries its events. That
  ///     was a description of the registration fault, not a contract: `SessionHost.Create`
  ///     registered the host-launching runtime and then the in-process one, and
  ///     last-registration-wins made the second win. So every <c>agent.spawn</c> inside a host
  ///     ran in the conversation host - the condition issue #305 D1 exists to end - and because
  ///     no <c>start</c> envelope was ever sent, nothing registered the leaf mailbox a cell's
  ///     steer is delivered to, so the app's steer was answered <c>NotRunning</c>.</para>
  ///     <para>THE ROOT FORWARDER IS STILL WHAT CARRIES A ROOT-CONTAINER CHILD'S EVENTS: the two
  ///     tests above publish onto the root container's stream directly and assert arrival, and
  ///     they are unchanged. A child a HOST launches has its own host and its own per-child
  ///     forwarder (<c>RunChildAsync</c>), which is the path the app actually takes.</para></summary>
  [Fact]
  public async Task HostContainer_LaunchesAHostPerAgent_WhileKeepingItsLeafExecutorInProcess()
  {
    HostRig rig = await HostRig.StartAsync().ConfigureAwait(true);
    try
    {
      RootRunHost? runner = await rig.Server.ResolveRootHostForTest().ConfigureAwait(true);
      Assert.True(runner is not null);

      // The orchestration seam launches a host per agent, so an agent.spawn does not run here.
      object runtime = runner.Services.GetRequiredService(typeof(IAgentRuntime));
      _ = Assert.IsType<HostLaunchingRuntime>(runtime);

      // The leaf executor still exists and is still in-process: it runs the one agent THIS host
      // owns, which is what the server dispatches a start envelope through.
      object leaf = runner.Services.GetRequiredService(typeof(InProcessAgentRuntime));
      _ = Assert.IsType<InProcessAgentRuntime>(leaf);
      Assert.NotSame(runtime, leaf);
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

    /// <summary>The APP-side stream the Desktop's badge subscribes.</summary>
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
    public async Task<AgentEvent?> WaitForEventAsync(AgentId agentId)
    {
      DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
      while (DateTimeOffset.UtcNow < deadline)
      {
        if (Events.Published.Find(e => e.Id == agentId) is { } found)
        {
          return found;
        }

        await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
      }

      return Events.Published.Find(e => e.Id == agentId);
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
    private readonly List<AgentEvent> _published = [];

    public List<AgentEvent> Published
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

    public void Publish(AgentEvent evt)
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
