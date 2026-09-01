#pragma warning disable xUnit1051 // TickOnceAsync has no ct parameter to pass TestContext's token through
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Host-side idle detection (handoff item 2): the host runs a watchdog over
///     every child it executes, fed by the child container's own event stream, with
///     the policy's retry/terminal enacted against the host's OWN runtime — never
///     app-side guessing from absent beats. Before this existed, a hung-but-under-
///     budget remote child ran forever.</summary>
public class HostChildWatchdogTests
{
  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class FakeListStore : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];
    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success("saved"));
    }
    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
      Records[record.Id.Value] = record;
      return Task.FromResult(Result.Success("updated"));
    }
    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", "no record")));
    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));
    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id.ToString()));
    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  private sealed class RecordingRuntime : IAgentRuntime
  {
    public List<AgentId> Interrupts { get; } = [];
    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success(record.Id));
    public Result<bool> Deliver(AgentId id, PendingMessage message) => Result.Success(true);
    public Task<Result<AgentRunOutcome>> WhenSettledAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<AgentRunOutcome>(new DomainError("NotFound", "no run")));
    public void InterruptSubtree(AgentId rootOfSubtree) => Interrupts.Add(rootOfSubtree);
    public void Interrupt(AgentId? childId = null)
    {
      if (childId is { } id)
      {
        Interrupts.Add(id);
      }
    }
  }

  private sealed class FakeHeartbeat : IAgentHeartbeat
  {
    public void Beat(AgentId agentId) { }
    public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
    {
      lastBeat = default;
      return false;
    }
    public void Forget(AgentId agentId) { }
  }

  private sealed class FakeAudit : IWatchdogEventStore
  {
    public List<WatchdogEvent> Rows { get; } = [];
    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      Rows.Add(evt);
      return Task.FromResult(Result.Success(evt.Id.ToString()));
    }
    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([.. Rows.Take(limit)]));
    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Rows.Count(e => e.AgentId == agentId && e.Kind == kind)));
  }

  private sealed class FixedMetrics : IProcessMetrics
  {
    public long WorkingSetBytes() => 1024L * 1024 * 1024;
  }

  private sealed class TestStream : IAgentEvents
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
      public void Dispose() { }
    }
  }

  private static WatchdogServices Services(StubClock clock, FakeListStore store, RecordingRuntime runtime,
      FakeHeartbeat heartbeat, FakeAudit audit, TestStream stream, ChildSupervisorRegistry supervisors)
    => new(store, runtime, heartbeat, audit,
        new WatchdogPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), 0),
        new FixedMetrics(), new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60)), clock,
        stream, supervisors);

  [Fact]
  public async Task Never_Beating_Child_Past_Threshold_Is_Interrupted_By_The_Host()
  {
    StubClock clock = new(DateTimeOffset.UtcNow);
    FakeListStore store = new();
    RecordingRuntime runtime = new();
    TestStream stream = new();
    ChildSupervisorRegistry supervisors = new();
    AgentId child = new(Guid.NewGuid());
    _ = await store.SaveAsync(AgentRecord.Spawned(child, null, 1, "m/sub", "hung", "task", DateTimeOffset.UtcNow));
    // The runtime registers the child's supervisor at Start; a never-beating run is
    // idle from OnStart onward (its clock = the watchdog's clock in production).
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    supervisors.Register(child, supervisor);

    HostChildWatchdog watchdog = new(new AgentId(Guid.NewGuid()), Services(clock, store, runtime, new FakeHeartbeat(), new FakeAudit(), stream, supervisors),
        tickInterval: TimeSpan.FromMilliseconds(20));
    watchdog.Start();

    clock.Now = clock.Now.AddMinutes(2); // silent the whole time: past the 1m threshold
    await watchdog.TickOnceAsync();

    Assert.Contains(runtime.Interrupts, id => id == child);
    await watchdog.DisposeAsync().ConfigureAwait(true);
  }

  [Fact]
  public async Task Fed_Child_Is_Spared_Then_Acted_On_When_Silent()
  {
    StubClock clock = new(DateTimeOffset.UtcNow);
    FakeListStore store = new();
    RecordingRuntime runtime = new();
    TestStream stream = new();
    ChildSupervisorRegistry supervisors = new();
    AgentId child = new(Guid.NewGuid());
    _ = await store.SaveAsync(AgentRecord.Spawned(child, null, 1, "m/sub", "worker", "task", DateTimeOffset.UtcNow));
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    supervisors.Register(child, supervisor);

    HostChildWatchdog watchdog = new(new AgentId(Guid.NewGuid()), Services(clock, store, runtime, new FakeHeartbeat(), new FakeAudit(), stream, supervisors),
        tickInterval: TimeSpan.FromMilliseconds(20));
    watchdog.Start();

    // Alive at +30s (fed by a progress event), then silent: acted on only after silence.
    clock.Now = clock.Now.AddSeconds(30);
    stream.Publish(new ChildProgressEvent(child, clock.Now, ChildPhase.ToolExec, "tool:edit"));
    await watchdog.TickOnceAsync();
    Assert.DoesNotContain(runtime.Interrupts, id => id == child);

    clock.Now = clock.Now.AddMinutes(2);
    await watchdog.TickOnceAsync();
    Assert.Contains(runtime.Interrupts, id => id == child);
    await watchdog.DisposeAsync().ConfigureAwait(true);
  }
}
