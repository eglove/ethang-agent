#pragma warning disable xUnit1051 // TickAsync has no ct parameter to pass TestContext's token through
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Stream-stall observation at the tick: appends a StreamStalled audit row and
///     publishes the event, but NEVER interrupts — the stall is informational.
///     Also pins WatchdogPolicy's stall-threshold defaults and validation.</summary>
public class AgentWatchdogStallTests
{
  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
  }

  private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

  private static AgentRecord Root() => AgentRecord.Spawned(
      AgentId.NewId(), null, 0, "m", "root", "root", T0);

  private static AgentRecord Child(AgentId parent, DateTimeOffset createdAt) => AgentRecord.Spawned(
      AgentId.NewId(), parent, 1, "m", "child", "task", createdAt);

  private sealed class FakeStore : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];
    public List<AgentRecord> Updated { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
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
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
      => Task.FromResult(Result.Success(record.Id));

    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);

    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "not exercised by this fake.")));

    public Task<Result<AgentId>> Resume(AgentId id, string message, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentId>(new DomainError("ResumeUnsupported", "not exercised by this fake.")));

    public void InterruptSubtree(AgentId rootOfSubtree) { }

    public void Interrupt(AgentId? childId = null)
    {
      if (childId is { } id)
      {
        Interrupted.Add(id.Value);
      }
    }
  }

  private sealed class FakeHeartbeat : IAgentHeartbeat
  {
    public Dictionary<Guid, DateTimeOffset> Beats { get; } = [];

    public void Beat(AgentId agentId) => Beats[agentId.Value] = DateTimeOffset.UtcNow;

    public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
    {
      bool found = Beats.TryGetValue(agentId.Value, out DateTimeOffset value);
      lastBeat = value;
      return found;
    }

    public void Forget(AgentId agentId) => Beats.Remove(agentId.Value);
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
  private sealed class ChildEventStream : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new StreamLease();
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber subscriber in _subscribers.ToArray())
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class StreamLease : IDisposable
    {
      public void Dispose()
      {
      }
    }
  }
  [Fact]
  public async Task Tick_StallCrossed_AppendsStreamStalledRow_AndDoesNotInterrupt()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    FakeStore store = new();
    FakeRuntime runtime = new();
    FakeHeartbeat heartbeat = new();
    FakeEvents audit = new();
    ChildEventStream stream = new();
    ChildSupervisorRegistry supervisors = new();
    AgentId childId = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(childId, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    supervisors.Register(childId, supervisor);
    // Policy: idle threshold 15m (child stays under it), stall threshold 2m (child crosses it).
    WatchdogServices services = new(store, runtime, heartbeat, audit,
        new WatchdogPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), 1, TimeSpan.FromMinutes(2)),
        new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60)), clock,
        stream, supervisors);
#pragma warning disable CA2000 // Dispose via the harness pattern: the watchdog's lease dies with the test
    AgentWatchdog watchdog = new(root.Id, services);
#pragma warning restore CA2000 // Dispose via the harness pattern: the watchdog's lease dies with the test
    AgentRecord child = Child(root.Id, T0);
    store.Records[root.Id.Value] = root;
    store.Records[childId.Value] = child;

    clock.Now = T0.AddMinutes(3); // under idle (15m), past stall (2m)
    await watchdog.TickAsync();

    Assert.DoesNotContain(runtime.Interrupted, id => id == childId.Value); // observation only
    Assert.Contains(audit.Appended, e => e.Kind == WatchdogEventKind.StreamStalled);
  }

  [Fact]
  public async Task Tick_StreamFresh_NoRow()
  {
    AgentRecord root = Root();
    StubClock clock = new(T0);
    FakeStore store = new();
    FakeRuntime runtime = new();
    FakeHeartbeat heartbeat = new();
    FakeEvents audit = new();
    ChildEventStream stream = new();
    ChildSupervisorRegistry supervisors = new();
    AgentId childId = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(childId, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    supervisor.OnStreamActivity("content");
    supervisors.Register(childId, supervisor);
    WatchdogServices services = new(store, runtime, heartbeat, audit,
        new WatchdogPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), 1, TimeSpan.FromMinutes(2)),
        new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60)), clock,
        stream, supervisors);
#pragma warning disable CA2000 // Dispose via the harness pattern: the watchdog's lease dies with the test
    AgentWatchdog watchdog = new(root.Id, services);
#pragma warning restore CA2000 // Dispose via the harness pattern: the watchdog's lease dies with the test
    AgentRecord child = Child(root.Id, T0);
    store.Records[root.Id.Value] = root;
    store.Records[childId.Value] = child;

    clock.Now = T0.AddMinutes(1); // under the 2-minute stall threshold
    await watchdog.TickAsync();

    Assert.DoesNotContain(audit.Appended, e => e.Kind == WatchdogEventKind.StreamStalled);
  }

  [Fact]
  public void WatchdogPolicy_ParameterlessCtor_DefaultStallThreshold_IsTwoMinutes()
  {
    WatchdogPolicy policy = new();

    Assert.Equal(TimeSpan.FromMinutes(2), policy.StallThreshold);
  }

  [Fact]
  public void WatchdogPolicy_ExplicitZeroStallThreshold_Throws()
  {
    _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
        new WatchdogPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), 1, TimeSpan.Zero));
  }
}
