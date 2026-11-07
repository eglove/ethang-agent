using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ChildHost;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #330 review F3: a SPAWNED agent's host must actually have a mailbox a steer
///     can be delivered into, and the registration that puts it there belongs to
///     <c>RunChildAsync</c>.
///     <para>THE DEFECT THIS CLOSES: every pre-existing steer test attached the mailbox through
///     the public <c>AttachAgentSteering</c> seam, so they proved 'a hand-attached mailbox is
///     steered' and never 'the production path registers one'. Underneath that gap sat a real
///     ordering fault: <c>SessionHost.Create</c> registered the host-launching runtime and then
///     the in-process runtime as <c>IAgentRuntime</c>, and last-registration-wins meant every
///     <c>agent.spawn</c> inside a host ran IN the conversation host. No start envelope was
///     sent, so <c>RunChildAsync</c> never ran, so nothing registered a leaf inbox - and the
///     app's steer was answered <c>NotRunning</c>.</para>
///     <para>WHAT THIS DRIVES: a REAL <see cref="ChildHostServer"/>, a REAL settings file, a REAL
///     database, a REAL <c>start</c> envelope, and the REAL <c>RunChildAsync</c> it dispatches
///     to - including the container that method builds for itself. Nothing here attaches a seam
///     by hand, which is the whole point: a test that did could not tell the fix from the bug.
///     The only double is the MODEL, reached the way production reaches it - over HTTP at the
///     base URL the settings name.</para>
///     <para>THE ASSERTION IS THE AGENT'S OWN CONVERSATION: not 'an ack said yes', but 'the
///     agent's next turn was handed the text'. A mailbox registered but never drained by the loop
///     the run actually uses would satisfy an ack and fail this.</para></summary>
public class ChildHostSpawnedAgentSteerTests
{
  private static readonly TimeSpan Limit = TestCeiling.Value;

  private const string ChildModel = "mock/spawned-agent";

  /// <summary>THE PRODUCTION PATH, END TO END: a spawned agent's host registers its mailbox
  ///     through <c>RunChildAsync</c>, an <c>agent-steer</c> envelope is accepted by that host,
  ///     and the agent's own loop drains the text into the conversation its next turn reads.
  ///     <para>A STEER THE RUN HAS NOT DRAINED YET IS THE ORDINARY CASE: the mock model holds
  ///     the first request open while the steer is sent, so the drain genuinely happens at a
  ///     safe point rather than by arriving before the run existed.</para></summary>
  [Fact]
  public async Task ASpawnedAgentsHost_RegistersItsMailbox_AndItsLoopDrainsASteer()
  {
    using Rig rig = Rig.Create();
    AgentRecord record = await rig.PersistAgentAsync().ConfigureAwait(true);

    // The app side, over a real pipe, driving the real server.
#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await rig.ConnectAsync().ConfigureAwait(true);
#pragma warning restore CA2007
    await app.SendAsync(new TransportEnvelope("start",
        JsonSerializer.Serialize(new StartCommand(record.Id.Value, MaxConcurrent: 4, record.ModelUsed)), 1),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The run is live once its first provider request has been made: RunChildAsync registers the
    // leaf mailbox BEFORE it dispatches the run, so a steer from here on has a box to land in.
    await rig.WaitForFirstRequestAsync(record.Id).ConfigureAwait(true);

    Guid pinned = Guid.NewGuid();
    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(record.Id.Value, SteerMarker, "human", pinned)), 2),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    AgentSteerAck ack = await ReadAckAsync(app, record.Id).ConfigureAwait(true);
    Assert.Null(ack.ErrorCode);

    // Let the held request answer, so the loop reaches its next safe point and drains the box.
    rig.Mock.Release();

    // THE PROOF: a turn was handed the steered text, so the message reached the mailbox the
    // run's own loop drains - in the host that run actually lives in.
    await rig.WaitForSteerInARequestAsync().ConfigureAwait(true);
  }

  /// <summary>THE PRODUCTION TOPOLOGY (issue #330 review F3): the agent runs in a NESTED host the
  ///     conversation host launched, and the app holds a connection to the CONVERSATION host only.
  ///     <para>THE DEFECT THIS PINS: with the ordering fix in place, `agent.spawn` inside the
  ///     conversation host launches a host of its own, so <c>RunChildAsync</c> registers the leaf
  ///     mailbox on THAT host - a server the app holds no pipe to. The app's `agent-steer` then
  ///     landed on the conversation host, which held no box for the agent and answered
  ///     <c>NotRunning</c>. The mailbox existed, was registered, and was still unreachable: the
  ///     acceptance item was false in the shape production actually runs.</para>
  ///     <para>WHAT MAKES THIS THE REAL SHAPE: two <see cref="ChildHostServer"/>s over one settings
  ///     file and one database, and the app's steer sent to the CONVERSATION server's pipe - never
  ///     to the nested one, which production cannot dial. The conversation host must recognise
  ///     that it launched this agent and forward the steer over the child's own connection.</para>
  ///     <para>THE ASSERTION IS THE AGENT'S OWN TURN: not the ack, but the text the nested host's
  ///     run was actually handed. An ack claiming success with nothing delivered would pass
  ///     anything weaker.</para></summary>
  [Fact]
  public async Task ASteerSentToTheConversationHost_ReachesTheAgentInItsNestedHost()
  {
    using Rig rig = Rig.Create();
    AgentRecord record = await rig.PersistAgentAsync().ConfigureAwait(true);

    // The app's side of the CONVERSATION host - the only connection it holds.
#pragma warning disable CA2007 // Named decision: 'await using' cannot carry ConfigureAwait.
    await using NamedPipeChildTransport app = await rig.ConnectAsync().ConfigureAwait(true);
#pragma warning restore CA2007

    // The ROOT CONVERSATION's own container launches the agent into a host of its own - exactly
    // what an agent.spawn inside the conversation does, and the object that holds the child's
    // connection. Production reaches this same container through the server's root runner.
    RootRunHost root = await rig.RootRunnerAsync().ConfigureAwait(true);
    IAgentRuntime orchestration = root.Services.GetRequiredService<IAgentRuntime>();
    Result<AgentId> launched = await orchestration.Start(record, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(launched.IsSuccess, launched.Error?.Message);

    await rig.WaitForFirstRequestAsync(record.Id).ConfigureAwait(true);

    Guid pinned = Guid.NewGuid();
    await app.SendAsync(new TransportEnvelope("agent-steer",
        JsonSerializer.Serialize(new AgentSteerCommand(record.Id.Value, SteerMarker, "human", pinned)), 2),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    AgentSteerAck ack = await ReadAckAsync(app, record.Id).ConfigureAwait(true);
    Assert.Null(ack.ErrorCode);

    rig.Mock.Release();
    await rig.WaitForSteerInARequestAsync().ConfigureAwait(true);
  }

  /// <summary>THE ORDERING FAULT ITSELF, asserted directly: a host container resolves the
  ///     HOST-LAUNCHING runtime as its orchestration seam and keeps the in-process runner as
  ///     its leaf executor. Before the fix the second registration silently replaced the first,
  ///     so every spawned agent ran inside the conversation host instead of reaching a host of
  ///     its own - and no start envelope was ever sent, so no leaf mailbox was registered.
  ///     </summary>
  [Fact]
  public void AHostsOrchestrationSeam_LaunchesAHost_WhileItsLeafExecutorStaysInProcess()
  {
    using Rig rig = Rig.Create();

    object orchestration = rig.Host.Services.GetRequiredService(typeof(IAgentRuntime));
    object leaf = rig.Host.Services.GetRequiredService(typeof(InProcessAgentRuntime));

    // The orchestration seam must NOT be the leaf executor, or a spawn never leaves this process.
    _ = Assert.IsType<HostLaunchingRuntime>(orchestration);
    _ = Assert.IsType<InProcessAgentRuntime>(leaf);
    Assert.NotSame(orchestration, leaf);

    // The property the host itself exposes resolves the same seam, which is the one an
    // agent.spawn inside this host asks for.
    _ = Assert.IsType<HostLaunchingRuntime>(rig.Host.Runtime);
  }

  /// <summary>The text this test steers with. Distinctive so a match cannot be an accident of
  ///     the agent's own task prompt.</summary>
  private const string SteerMarker = "STEER-MARKER-8f21";

  /// <summary>Reads frames until the ack for <paramref name="agent"/> arrives, ignoring the
  ///     host's declare frames.</summary>
  private static async Task<AgentSteerAck> ReadAckAsync(NamedPipeChildTransport app, AgentId agent)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      TransportEnvelope envelope = await app.ReceiveAsync()
          .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (envelope.Kind != "agent-steer-ack")
      {
        continue;
      }

      AgentSteerAck? ack = JsonSerializer.Deserialize<AgentSteerAck>(envelope.Json);
      if (ack?.AgentId == agent.Value)
      {
        return ack;
      }
    }

    Assert.Fail("no agent-steer ack arrived for " + agent + " within the limit");
    throw new InvalidOperationException("unreachable");
  }

  /// <summary>The real host stack: a settings file and database the host builds its own
  ///     container from, and a local provider the settings' base URL points at.
  ///     <para>WHY THE MOCK IS AN HTTP SERVER: <c>RunChildAsync</c> builds its container from
  ///     the settings file, so a registration overlay here would apply to a DIFFERENT container
  ///     than the one the run uses - the green-test-over-a-broken-real-path shape. Pointing the
  ///     shipped base URL at a local server is the only substitution that reaches the real run.
  ///     </para></summary>
  private sealed class Rig(string root, ChildHostServer server, SessionHost host, MockModel mock)
      : IDisposable
  {
    private readonly string _root = root;
    private readonly ChildHostServer _server = server;

    /// <summary>The host container the server's own build produces - the container under test.
    ///     <para>A SEPARATE INSTANCE from the one a run uses, exactly as in production: it is
    ///     the container a caller inspects, and its registrations are the ones the fix changed.
    ///     </para></summary>
    public SessionHost Host { get; } = host;

    /// <summary>The local provider the spawned run's model calls land on.</summary>
    public MockModel Mock { get; } = mock;

    public static Rig Create()
    {
      string root = TestTempRoot.CreateDirectory("ethang-spawned-steer").FullName;
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      string database = Path.Combine(root, "app.db");

      // The app owns and migrates the schema: the host is a GUEST and opens an existing file.
      // Named decision (CA2000): the database handle is process-wide state by design - the file
      // is opened for its migration, and the connection pool holds it for the run's lifetime.
#pragma warning disable CA2000 // The AppDatabase is intentionally not disposed here.
      _ = new AppDatabase(database);
#pragma warning restore CA2000

      // Named decision (CA2000): ownership transfers to the returned rig, whose Dispose
      // stops the listener; a failure below still leaves the process short-lived.
#pragma warning disable CA2000 // Ownership transfers to the Rig below.
      MockModel mock = new();
#pragma warning restore CA2000
      mock.Start();
      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", mock.BaseUrl),
          new AgentRunOptions(null, 4),
          WorkspaceRoot: workspace,
          SessionId: Guid.NewGuid().ToString("D"))
          .WithWorkspaceId(workspace);

      string settingsPath = Path.Combine(root, "host-settings.json");
      File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));

      return new Rig(root, new ChildHostServer(settingsPath, database),
          SessionHost.Create(settingsPath, database), mock);
    }

    /// <summary>The ROOT RUNNER the server itself resolves for root turns - the object a root
    ///     turn's `agent.spawn` runs through, and therefore the one holding any child it launches.
    ///     <para>RESOLVED FROM THE SERVER, not built here: a container this rig made would be a
    ///     DIFFERENT instance from the one the server forwards through, so a child launched from
    ///     it would be invisible to the path under test - green for the wrong reason.</para>
    ///     </summary>
    /// <returns>The server's own root runner.</returns>
    public async Task<RootRunHost> RootRunnerAsync()
    {
      RootRunHost? runner = await _server.ResolveRootHostForTest().ConfigureAwait(false);
      Assert.NotNull(runner);
      return runner;
    }

    /// <summary>Persists an agent record the launched run will load and execute.</summary>
    public async Task<AgentRecord> PersistAgentAsync()
    {
      AgentId id = AgentId.NewId();
      AgentRecord record = AgentRecord.Started(id, ChildModel,
          label: "spawned", taskPrompt: "do the work", DateTimeOffset.UtcNow,
          provider: Providers.OpenRouter, workspaceId: WorkspaceId);
      Result<string> saved = await Host.Store.SaveAsync(record, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(saved.IsSuccess, saved.Error?.Message);
      return record;
    }

    /// <summary>The workspace id the settings name, which the record must carry so the run
    ///     resolves a workspace.</summary>
    private string WorkspaceId => Path.Combine(_root, "ws");

    /// <summary>Waits until the agent's run has made a provider request, or fails with what the
    ///     row actually holds - a bare timeout would say nothing about which stage refused.
    ///     </summary>
    public async Task WaitForFirstRequestAsync(AgentId id)
    {
      DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
      while (Mock.Requests == 0)
      {
        if (DateTimeOffset.UtcNow > deadline)
        {
          Result<AgentRecord> row = await Host.Store.GetAsync(id, CancellationToken.None).ConfigureAwait(true);
          string state = row.IsSuccess
              ? $"status={row.Value.Status} reason={row.Value.FailureReason} report={row.Value.FinalReport}"
              : $"unreadable: {row.Error?.Message}";
          Assert.Fail("the run never called the model.\n  row: " + state);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
      }
    }

    /// <summary>Waits until a provider request carried the steered text, which is the drain made
    ///     observable at the only place that proves it: what the model was handed.</summary>
    public async Task WaitForSteerInARequestAsync()
    {
      DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
      while (!Mock.SawSteer)
      {
        if (DateTimeOffset.UtcNow > deadline)
        {
          Assert.Fail("the agent's next turn never saw the steered text; requests=" + Mock.Requests);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
      }
    }

    /// <summary>Connects the app side to the real server over a real named pipe.</summary>
    public async Task<NamedPipeChildTransport> ConnectAsync()
    {
      string pipeName = "ethang-spawned-steer-" + Guid.NewGuid().ToString("N");
      Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
          TestContext.Current.CancellationToken);
      NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      _server.AttachTransport(hostSide);
      _ = _server.ServeAsync();
      return app;
    }

    public void Dispose()
    {
      Mock.Dispose();
      try
      {
        Directory.Delete(_root, recursive: true);
      }
      catch (IOException)
      {
        // Left behind deliberately: a passing test must not fail on cleanup.
      }
    }
  }

  /// <summary>A local OpenAI-compatible provider: a catalog naming the child model, and a
  ///     responses endpoint that holds the FIRST request until the test releases it, so a steer
  ///     arrives while a turn is genuinely in flight. Every request body is kept, which is where
  ///     the drained message is observed.</summary>
  private sealed class MockModel : IDisposable
  {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<string> _bodies = [];
    private int _requests;
    private int _released;

    public Uri BaseUrl { get; private set; } = null!;

    /// <summary>How many responses requests have been received.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Whether any request body carried the steered text.</summary>
    public bool SawSteer
    {
      get
      {
        lock (_gate)
        {
          return _bodies.Any(body => body.Contains(SteerMarker, StringComparison.Ordinal));
        }
      }
    }

    public void Start()
    {
      using TcpListener probe = new(IPAddress.Loopback, 0);
      probe.Start();
      int port = ((IPEndPoint)probe.LocalEndpoint).Port;
      probe.Stop();
      BaseUrl = new Uri($"http://127.0.0.1:{port}/");
      _listener.Prefixes.Add(BaseUrl.AbsoluteUri);
      _listener.Start();
      _ = Task.Run(LoopAsync, _cts.Token);
    }

    /// <summary>Lets the held first request answer, so the loop reaches its next safe point.</summary>
    public void Release() => _ = Interlocked.Exchange(ref _released, 1);

    // Named decision (CA1031): the accept loop is the mock's liveness boundary - any single
    // accept fault (a listener race at shutdown) must not end the mock.
#pragma warning disable CA1031 // Do not catch general exception types
    private async Task LoopAsync()
    {
      while (!_cts.IsCancellationRequested)
      {
        HttpListenerContext ctx;
        try
        {
          ctx = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch
        {
          break;
        }

        try
        {
          await HandleAsync(ctx).ConfigureAwait(false);
        }
        catch
        {
          // Swallowed deliberately: see the named decision above.
        }
      }
    }
#pragma warning restore CA1031 // Do not catch general exception types

    private async Task HandleAsync(HttpListenerContext ctx)
    {
      string path = ctx.Request.Url!.AbsolutePath;
      if (path.EndsWith("/endpoints", StringComparison.Ordinal))
      {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        return;
      }

      if (path.EndsWith("/models", StringComparison.Ordinal))
      {
        await WriteAsync(ctx, CatalogJson).ConfigureAwait(false);
        return;
      }

      if (!path.EndsWith("/responses", StringComparison.Ordinal))
      {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        return;
      }

      using StreamReader reader = new(ctx.Request.InputStream, Encoding.UTF8);
      string body = await reader.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
      lock (_gate)
      {
        _bodies.Add(body);
      }

      if (Interlocked.Increment(ref _requests) == 1)
      {
        // HELD: the first turn stays in flight until the test has steered the run.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (Volatile.Read(ref _released) == 0 && DateTimeOffset.UtcNow < deadline)
        {
          await Task.Delay(TimeSpan.FromMilliseconds(25), _cts.Token).ConfigureAwait(false);
        }
      }

      await WriteAsync(ctx, CompletionJson).ConfigureAwait(false);
    }

    private async Task WriteAsync(HttpListenerContext ctx, string json)
    {
      byte[] bytes = Encoding.UTF8.GetBytes(json);
      ctx.Response.StatusCode = 200;
      ctx.Response.ContentType = "application/json";
      ctx.Response.ContentLength64 = bytes.Length;
      await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
      ctx.Response.Close();
    }

    private static string CatalogJson =>
        /*lang=json,strict*/ """{"data":[{"id":"mock/spawned-agent","context_length":32768,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"]}]}""";

    private static string CompletionJson =>
        /*lang=json,strict*/ """{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"child finished"}]}],"status":"completed"}""";

    public void Dispose()
    {
      _cts.Cancel();
      _cts.Dispose();
      _listener.Stop();
      _listener.Close();
    }
  }
}
