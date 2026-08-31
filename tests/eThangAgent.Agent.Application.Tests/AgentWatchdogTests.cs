#pragma warning disable xUnit1051 // TickAsync has no ct parameter to pass TestContext's token through
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The watchdog tick matrix: first breach retries with reset and Start, settle
///     outcomes branch correctly, deadline defers, second breach terminates Failed(Hung),
///     roots and heartbeat-less children are never touched, RSS is observe-only, and other
///     roots' children are invisible.</summary>
public class AgentWatchdogTests
{
  private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class FakeStore : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];
    public List<AgentRecord> Saved { get; } = [];
    public List<AgentRecord> Updated { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
      Saved.Add(record);
      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success("ok"));
    }

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
      Updated.Add(record);
      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success("ok"));
    }

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Success(Records[id.Value]));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
      => Task.FromResult(Result.Success("ok"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
      => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  private sealed class FakeRuntime : IAgentRuntime
  {
    public List<Guid> Interrupted { get; } = [];
    public List<Guid> Started { get; } = [];
    public Action<Guid>? OnInterrupt { get; set; }

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
    {
      Started.Add(record.Id.Value);
      return Task.FromResult(Result.Success(record.Id));
    }

    public Result<bool> Deliver(AgentId id, PendingMessage message)
      => Result.Success(true);

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
  => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", $"agent '{id}' has no live or settled run owned by this runtime.")));

    public void Interrupt(AgentId? childId = null)
    {
      if (childId is { } id)
      {
        Interrupted.Add(id.Value);
        OnInterrupt?.Invoke(id.Value);
      }
    }
  }

  private sealed class FakeHeartbeat : IAgentHeartbeat
  {
    public Dictionary<Guid, DateTimeOffset> Beats { get; } = [];
    public HashSet<Guid> Forgotten { get; } = [];

    public void Beat(AgentId agentId) => Beats[agentId.Value] = DateTimeOffset.UtcNow;

    public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
    {
      bool found = Beats.TryGetValue(agentId.Value, out DateTimeOffset value);
      lastBeat = value;
      return found;
    }

    public void Forget(AgentId agentId) => Forgotten.Add(agentId.Value);
  }

  private sealed class FakeEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Appended { get; } = [];

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Appended.Add(evt);
      return Task.FromResult(Result.Success(evt.Id.ToString()));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([.. Appended.Take(limit)]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
      => Task.FromResult(Result.Success(Appended.Count(e => e.AgentId == agentId && e.Kind == kind)));
  }

  private sealed class FakeMetrics(long bytes) : IProcessMetrics
  {
    public long WorkingSetBytes() => bytes;
  }

  private static AgentRecord Root() => AgentRecord.Spawned(
      AgentId.NewId(), null, 0, "m", "root", "root", T0);

  private static AgentRecord Child(AgentId parent, DateTimeOffset createdAt) => AgentRecord.Spawned(
      AgentId.NewId(), parent, 1, "m", "child", "task", createdAt);

  private static (AgentWatchdog Watchdog, FakeStore Store, FakeRuntime Runtime, FakeHeartbeat
      Heartbeat, FakeEvents Events, StubClock Clock) Harness(
          AgentId rootId, StubClock clock, TimeSpan? settlePoll = null, FakeMetrics? metrics = null)
  {
    FakeStore store = new();
    FakeRuntime runtime = new();
    FakeHeartbeat heartbeat = new();
    FakeEvents events = new();
    WatchdogServices services = new(store, runtime, heartbeat, events,
        new WatchdogPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), 1),
        metrics ?? new FakeMetrics(1024L * 1024 * 1024),
        new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60)), clock);
    AgentWatchdog watchdog = new(rootId, services);
    AgentWatchdog settled = settlePoll is { } poll ? watchdog.WithSettlePollInterval(poll) : watchdog;
    return (settled, store, runtime, heartbeat, events, clock);
  }

  /// <summary>Wires the common retry-test arrangement: a beat entry for the child and an
  ///     OnInterrupt hook that settles the run as Failed(Interrupted).</summary>
  private static void SettleOnInterrupt(FakeRuntime runtime, FakeStore store, FakeHeartbeat heartbeat,
      AgentId childId, DateTimeOffset beat)
  {
    heartbeat.Beats[childId.Value] = beat;
    runtime.OnInterrupt = id => store.Records[id] = store.Records[id] with
    {
      Status = AgentStatus.Failed,
      FailureReason = AgentFailureReason.Interrupted,
      CompletedAt = DateTimeOffset.UtcNow,
    };
  }

  [Fact]
  public async Task Tick_IdleChildFirstBreach_InterruptsResetsAndRestarts()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, FakeHeartbeat heartbeat, FakeEvents events, _) =
        Harness(root.Id, clock, settlePoll: TimeSpan.FromMilliseconds(1));
    AgentRecord child = Child(root.Id, T0 - TimeSpan.FromMinutes(30));
    store.Records[root.Id.Value] = root;
    store.Records[child.Id.Value] = child;
    SettleOnInterrupt(runtime, store, heartbeat, child.Id, T0 - TimeSpan.FromMinutes(20));

    await watchdog.TickAsync();

    Assert.Contains(child.Id.Value, runtime.Interrupted);
    Assert.Contains(child.Id.Value, runtime.Started);
    AgentRecord after = store.Records[child.Id.Value];
    Assert.Equal(AgentStatus.Running, after.Status);
    Assert.Null(after.CompletedAt);
    Assert.Null(after.FailureReason);
    Assert.Contains(events.Appended, e => e.Kind is WatchdogEventKind.HungDetected);
    Assert.Contains(events.Appended, e => e.Kind is WatchdogEventKind.RetrySpawned);
  }

  [Fact]
  public async Task Tick_ChildSettlesCompletedDuringSettleWait_NoRetry()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, FakeHeartbeat heartbeat, FakeEvents events, _) =
        Harness(root.Id, clock, settlePoll: TimeSpan.FromMilliseconds(1));
    AgentRecord child = Child(root.Id, T0 - TimeSpan.FromMinutes(30));
    store.Records[root.Id.Value] = root;
    store.Records[child.Id.Value] = child;
    heartbeat.Beats[child.Id.Value] = T0 - TimeSpan.FromMinutes(20);
    runtime.OnInterrupt = id => store.Records[id] = store.Records[id] with { Status = AgentStatus.Completed, FinalReport = "finished while watched" };

    await watchdog.TickAsync();

    Assert.DoesNotContain(child.Id.Value, runtime.Started);
    Assert.DoesNotContain(events.Appended, e => e.Kind is WatchdogEventKind.RetrySpawned);
  }

  [Fact]
  public async Task Tick_ChildNeverSettlesByDeadline_RetryDeferredNoStart()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, FakeHeartbeat heartbeat, FakeEvents events, _) =
        Harness(root.Id, clock, settlePoll: TimeSpan.FromMilliseconds(1));
    AgentRecord child = Child(root.Id, T0 - TimeSpan.FromMinutes(30));
    store.Records[root.Id.Value] = root;
    store.Records[child.Id.Value] = child;
    heartbeat.Beats[child.Id.Value] = T0 - TimeSpan.FromMinutes(20);
    runtime.OnInterrupt = null; // cancel never observed: the record stays Running

    await watchdog.TickAsync();

    Assert.DoesNotContain(child.Id.Value, runtime.Started);
    Assert.Contains(events.Appended, e => e.Kind is WatchdogEventKind.RetryDeferred);
    Assert.Equal(AgentStatus.Running, store.Records[child.Id.Value].Status);
  }

  [Fact]
  public async Task Tick_SecondBreach_TerminalFailedHung()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, FakeHeartbeat heartbeat, FakeEvents events, _) =
        Harness(root.Id, clock, settlePoll: TimeSpan.FromMilliseconds(1));
    AgentRecord child = Child(root.Id, T0 - TimeSpan.FromMinutes(30));
    store.Records[root.Id.Value] = root;
    store.Records[child.Id.Value] = child;
    SettleOnInterrupt(runtime, store, heartbeat, child.Id, T0 - TimeSpan.FromMinutes(20));
    _ = await events.AppendAsync(new WatchdogEvent(Guid.NewGuid(), child.Id,
        WatchdogEventKind.RetrySpawned, "prior retry", 1, null, T0));

    await watchdog.TickAsync();

    AgentRecord after = store.Records[child.Id.Value];
    Assert.Equal(AgentStatus.Failed, after.Status);
    Assert.Equal(AgentFailureReason.Hung, after.FailureReason);
    Assert.NotNull(after.FinalReport);
    Assert.True(after.FinalReport is not null && after.FinalReport.Contains("Error [Hung]", StringComparison.Ordinal));
    Assert.Contains(events.Appended, e => e.Kind is WatchdogEventKind.TerminalReport);
    Assert.DoesNotContain(child.Id.Value, runtime.Started);
  }

  [Fact]
  public async Task Tick_RootNeverTouched()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, _, FakeEvents events, _) =
        Harness(root.Id, clock);
    store.Records[root.Id.Value] = root; // Running root, no heartbeat entry

    await watchdog.TickAsync();

    Assert.DoesNotContain(root.Id.Value, runtime.Interrupted);
    Assert.DoesNotContain(events.Appended, e => e.AgentId == root.Id);
  }

  [Fact]
  public async Task Tick_ChildWithoutHeartbeatEntry_Watched()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, _, FakeEvents events, _) =
        Harness(root.Id, clock);
    AgentRecord child = Child(root.Id, T0 - TimeSpan.FromHours(2));
    store.Records[root.Id.Value] = root;
    store.Records[child.Id.Value] = child; // stale CreatedAt, NO beat entry

    await watchdog.TickAsync();

    Assert.DoesNotContain(child.Id.Value, runtime.Interrupted);
    Assert.DoesNotContain(child.Id.Value, runtime.Started);
    Assert.DoesNotContain(events.Appended, e => e.AgentId == child.Id);
  }

  [Fact]
  public async Task Tick_RssAboveThreshold_RecordsOnceThenRateLimits()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, _, _, _, FakeEvents events, _) =
        Harness(root.Id, clock, metrics: new FakeMetrics(5000L * 1024 * 1024));

    await watchdog.TickAsync();
    await watchdog.TickAsync(); // no clock advance: rate-limited
    clock.Now = T0 + TimeSpan.FromMinutes(11);
    await watchdog.TickAsync(); // past the 10 min re-report interval

    Assert.Equal(2, events.Appended.Count(e => e.Kind is WatchdogEventKind.RssBreached));
  }

  [Fact]
  public async Task Tick_ChildOfAnotherRoot_Ignored()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    (AgentWatchdog watchdog, FakeStore store, FakeRuntime runtime, FakeHeartbeat heartbeat, _, _) =
        Harness(root.Id, clock);
    AgentRecord otherRoot = Root();
    AgentRecord otherChild = Child(otherRoot.Id, T0 - TimeSpan.FromMinutes(30));
    store.Records[root.Id.Value] = root;
    store.Records[otherRoot.Id.Value] = otherRoot;
    store.Records[otherChild.Id.Value] = otherChild;
    heartbeat.Beats[otherChild.Id.Value] = T0 - TimeSpan.FromMinutes(20);
    runtime.OnInterrupt = id => store.Records[id] = store.Records[id] with { Status = AgentStatus.Failed, FailureReason = AgentFailureReason.Interrupted };

    await watchdog.TickAsync();

    Assert.DoesNotContain(otherChild.Id.Value, runtime.Interrupted);
    Assert.DoesNotContain(otherChild.Id.Value, runtime.Started);
  }
}
