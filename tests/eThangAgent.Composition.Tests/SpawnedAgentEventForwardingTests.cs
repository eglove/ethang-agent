using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp cleanup is deliberate (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #326: a spawned agent's events reach the app. The defect was one
///     argument — <c>HostLaunchingRuntime</c> built each launched host's wire runtime with
///     <c>events: null</c>, so <c>RemoteAgentRuntime.HandleAgentEvent</c> dropped every
///     forwarded envelope at the process boundary and the app never observed a spawned
///     agent's lifecycle: nothing downstream could observe a spawn because there was never
///     an event to observe, not because a consumer was mis-wired.
///     <para>These tests drive the REAL composition — <c>AddHostLaunchingAgentRuntime</c>,
///     resolved from a container the way <c>SessionHost.Create</c> resolves it — against a
///     REAL child host process and a local mock provider. Asserting on the container's own
///     <c>IAgentEvents</c> is the point: a test that subscribed a hand-built stream would
///     pass with the null still in place.</para>
///     <para>Bounded awaits throughout (deadlock vigilance): every wait carries a timeout,
///     and every launched host is disposed through the runtime that owns it.</para></summary>
[Trait("Category", "Process")]
public class SpawnedAgentEventForwardingTests
{
  private const string ChildModel = "mock/child-model";

  /// <summary>Collects every event a container's stream publishes. Thread-safe: the pump
  ///     publishes from a background task while the test awaits the settle.</summary>
  private sealed class Collector : IAgentEventSubscriber
  {
    private readonly Lock _gate = new();
    private readonly List<AgentEvent> _events = [];

    public void OnEvent(AgentEvent evt)
    {
      lock (_gate)
      {
        _events.Add(evt);
      }
    }

    public IReadOnlyList<AgentEvent> Snapshot()
    {
      lock (_gate)
      {
        return [.. _events];
      }
    }
  }

  /// <summary>One composed parent container over a temp database, workspace, and mock
  ///     provider — the production shape a conversation host builds.</summary>
  private sealed class ParentHost : IAsyncDisposable
  {
    private readonly ServiceProvider _services;
    private readonly MockProviderServer _mock;
    private readonly string _root;

    private ParentHost(ServiceProvider services, MockProviderServer mock, string root, Collector collector)
    {
      _services = services;
      _mock = mock;
      _root = root;
      Events = collector;
    }

    public Collector Events { get; }

    public List<string> Notices { get; } = [];

    public HostLaunchingRuntime Runtime => _services.GetRequiredService<HostLaunchingRuntime>();

    public IAgentStore Store => _services.GetRequiredService<IAgentStore>();

    public string WorkspaceRoot { get; private set; } = "";

    /// <summary>Builds the container. <paramref name="wireEvents"/> false reproduces the
    ///     legacy shape: the runtime is constructed with no stream, exactly as the
    ///     pre-#326 code did.</summary>
    public static ParentHost Create(bool wireEvents)
    {
      string root = TestTempRoot.CreateDirectory("ethang-agent-events").FullName;
      string workspace = Directory.CreateDirectory(Path.Combine(root, "ws")).FullName;
      string database = Path.Combine(root, "app.db");
      MockProviderServer mock = new(MockModel.Of(ChildModel))
      {
        CompletionText = "child finished",
      };
      mock.Start();

      AgentSettings settings = new AgentSettings(
          new OpenRouterSettings("sk-test", mock.BaseUrl),
          new AgentRunOptions(null, 2))
          .WithWorkspaceRoot(workspace)
          .WithWorkspaceId(workspace);

      // The child host is a GUEST: it opens an EXISTING database, so the parent owns and
      // migrates the schema first.
      AppDatabase db = new(database);
      Collector collector = new();
      ServiceCollection services = new();
      _ = services.AddEThangAgentCore(settings, Providers.OpenRouter,
          ModelConfig.Create(ChildModel, 4096, 0.5f, 8192).Value!,
          new AgentHostOptions(FixedWorkspaceContext.ForPath(workspace), new UnrootedPathResolver()),
          db);
      if (wireEvents)
      {
        _ = services.AddHostLaunchingAgentRuntime(settings, database);
      }
      else
      {
        // The legacy wiring: a launcher with NO event stream. Null keeps today's
        // byte-identical behavior for a headless rig.
        _ = services.AddSingleton(sp => new HostLaunchingRuntime(
            settings, database,
            new HostAdmissionGate(4),
            notice => sp.GetRequiredService<SessionNoticeSink>().Post(notice))
        {
          Store = sp.GetRequiredService<IAgentStore>(),
        });
        _ = services.AddSingleton<IAgentRuntime>(sp => sp.GetRequiredService<HostLaunchingRuntime>());
      }

      ServiceProvider provider = services.BuildServiceProvider();
      _ = provider.GetRequiredService<IAgentEvents>().Subscribe(collector);
      ParentHost host = new(provider, mock, root, collector) { WorkspaceRoot = workspace };
      provider.GetRequiredService<SessionNoticeSink>().Sink = host.Notices.Add;
      return host;
    }

    /// <summary>Persists a child record the launched host will load and run.</summary>
    public async Task<AgentRecord> PersistChildAsync(string prompt)
    {
      AgentId id = AgentId.NewId();
      AgentRecord record = AgentRecord.Started(id, ChildModel,
          label: "child", taskPrompt: prompt, DateTimeOffset.UtcNow,
          contract: null, provider: Providers.OpenRouter, workspaceId: WorkspaceRoot);
      Result<string> saved = await Store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
      Assert.True(saved.IsSuccess, saved.Error?.Message);
      return record;
    }

    public async ValueTask DisposeAsync()
    {
      await _services.DisposeAsync().ConfigureAwait(false);
      _mock.Dispose();
      try
      {
        Directory.Delete(_root, recursive: true);
      }
      catch
      {
        // Left behind deliberately: a passing test must not fail on cleanup.
      }
    }
  }

  /// <summary>Waits until the parent's stream carries a settled fact for the child, or
  ///     fails with what the row and stream actually hold. The settle FACT is the assertion
  ///     under test (issue #326); waiting on a runtime await would test a different seam.</summary>
  private static async Task<AgentSettledEvent> AwaitSettledAsync(ParentHost parent, AgentRecord child)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (DateTimeOffset.UtcNow < deadline)
    {
      AgentSettledEvent? settled = parent.Events.Snapshot()
          .OfType<AgentSettledEvent>()
          .FirstOrDefault(e => e.Id == child.Id);
      if (settled is not null)
      {
        return settled;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    Assert.Fail(Diagnose(parent, child));
    throw new UnreachableException();
  }

  private static string Diagnose(ParentHost parent, AgentRecord child)
  {
    Result<AgentRecord> row = parent.Store.GetAsync(child.Id, CancellationToken.None).GetAwaiter().GetResult();
    string state = row.IsSuccess
        ? $"status={row.Value.Status} reason={row.Value.FailureReason} attempts={row.Value.Attempts} report={row.Value.FinalReport}"
        : $"unreadable: {row.Error?.Message}";
    IReadOnlyList<AgentEvent> seen = parent.Events.Snapshot();
    return "settle never arrived.\n  row: " + state
        + "\n  events: " + (seen.Count == 0 ? "(none)" : string.Join(", ", seen.Select(e => e.GetType().Name)))
        + "\n  notices: " + (parent.Notices.Count == 0 ? "(none)" : string.Join(" | ", parent.Notices));
  }

  [Fact]
  public async Task SpawnedAgent_PublishesStartedProgressAndSettledOntoTheParentsStream()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    await using ParentHost parent = ParentHost.Create(wireEvents: true);
    AgentRecord child = await parent.PersistChildAsync("do the work").ConfigureAwait(true);

    Result<AgentId> started = await parent.Runtime.Start(child, ct).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);

    AgentSettledEvent settled = await AwaitSettledAsync(parent, child).ConfigureAwait(true);
    Assert.Equal(AgentStatus.Completed, settled.TerminalStatus);

    IReadOnlyList<AgentEvent> events = parent.Events.Snapshot();
    // Acceptance: the started fact is what tells the app a cell should exist.
    AgentStartedEvent begun = Assert.Single(events.OfType<AgentStartedEvent>(),
        e => e.Id == child.Id);
    Assert.Equal(ChildModel, begun.ModelId);

    // Acceptance: progress beats arrive DURING the run — the live phase chip reads them.
    Assert.Contains(events.OfType<AgentProgressEvent>(), e => e.Id == child.Id);

    // Acceptance: no event is published twice. The launched host forwards each event
    // once, so the parent's stream holds exactly the facts the child published — a
    // second republish path would show up here as a duplicate instance.
    Assert.Equal(events.Count(e => e.Id == child.Id),
        events.Where(e => e.Id == child.Id).Distinct().Count());
  }

  [Fact]
  public async Task LauncherWithoutAnEventStream_StillStartsAndSettlesTheAgent()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    await using ParentHost parent = ParentHost.Create(wireEvents: false);
    AgentRecord child = await parent.PersistChildAsync("do the work").ConfigureAwait(true);

    Result<AgentId> started = await parent.Runtime.Start(child, ct).ConfigureAwait(true);
    Assert.True(started.IsSuccess, started.Error?.Message);

    // The run completes on the host either way — the legacy shape changes what the APP
    // observes, never what the agent does.
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (DateTimeOffset.UtcNow < deadline)
    {
      Result<AgentRecord> row = await parent.Store.GetAsync(child.Id, ct).ConfigureAwait(true);
      if (row.IsSuccess && row.Value.Status is AgentStatus.Completed or AgentStatus.Failed)
      {
        Assert.Equal(AgentStatus.Completed, row.Value.Status);
        // The legacy shape drops forwarded events, byte-identical to before the fix.
        Assert.DoesNotContain(parent.Events.Snapshot(), e => e.Id == child.Id);
        return;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(true);
    }

    Assert.Fail(Diagnose(parent, child));
  }
}
