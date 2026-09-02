using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>W1.3: the supervisor feed's per-event-kind contract, pinned so no future
///     contributor re-decides it silently. The one decision the spec asked to be argued:
///     a budget alert must NOT beat — it is not progress (near-zero burn while alerts
///     fire is strong stuck evidence), and it is published from inside the supervisor's
///     non-reentrant lock, which a feed echo would deadlock. Preemption likewise does
///     not beat: the receiver is interrupted right after the event publishes, and the
///     interrupt must be allowed to stall the idle window. Started events DO beat: the
///     runtime mints a fresh supervisor per (re)start, so the retry's started event is
///     the new run's liveness fact.</summary>
public class SupervisorFeedContractTests
{
  private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class FakeStream : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];
    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease();
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber s in _subscribers.ToArray())
      {
        s.OnEvent(evt);
      }
    }

    private sealed class Lease : IDisposable
    {
      public void Dispose() { }
    }
  }

  /// <summary>A supervisor whose idle window started at T0, registered under its id,
  ///     with the feed subscribed to the shared stream.</summary>
  private static (FakeStream Stream, ChildSupervisor Supervisor, AgentId Child, StubClock Clock) Fresh()
  {
    FakeStream stream = new();
    ChildSupervisorRegistry registry = new();
    StubClock clock = new(T0);
    AgentId child = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: new BudgetCeilings(MaxTokens: 1_000));
    supervisor.OnStart(0);
    registry.Register(child, supervisor);

    // Named decision (CA2000): the lease lives for the test method's duration — the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(new SupervisorFeed(registry));
#pragma warning restore CA2000
    return (stream, supervisor, child, clock);
  }

  [Fact]
  public void BudgetAlert_DoesNotBeat()
  {
    (FakeStream stream, ChildSupervisor supervisor, AgentId child, StubClock clock) = Fresh();

    stream.Publish(new ChildBudgetAlertEvent(child, T0, "tokens", 800, 1_000, 0.0));

    clock.Now = T0.AddMinutes(15).AddTicks(1);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert); // no beat: the idle window was NOT reset by the budget alert
  }

  [Fact]
  public void Preempted_DoesNotBeat()
  {
    (FakeStream stream, ChildSupervisor supervisor, AgentId child, StubClock clock) = Fresh();

    stream.Publish(new PreemptedEvent(child, T0, "agent:" + Guid.NewGuid(), (int)MessageUrgency.Urgent));

    clock.Now = T0.AddMinutes(15).AddTicks(1);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert); // no beat: preemption must be allowed to stall the idle window
  }

  [Fact]
  public void MessageDelivered_DoesNotBeat()
  {
    (FakeStream stream, ChildSupervisor supervisor, AgentId child, StubClock clock) = Fresh();

    stream.Publish(new MessageDeliveredEvent(child, T0, "inbound", 0, 64));

    clock.Now = T0.AddMinutes(15).AddTicks(1);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert); // no beat: mailbox lifecycle is not run progress
  }

  [Fact]
  public void IdleAlert_FeedsNothing_AndPreservesTheAlertItself()
  {
    (FakeStream stream, ChildSupervisor supervisor, _, StubClock clock) = Fresh();

    clock.Now = T0.AddMinutes(15).AddTicks(1);
    ChildIdleAlertEvent? first = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(first);

    // A second CheckIdle inside the alert's own publish would either deadlock on the
    // non-reentrant lock or clear _idleAlerted (re-arming the episode). The feed
    // ignores idle alerts outright — assert the observable half of both hazards:
    // no deadlock, and the episode stays marked (no re-arm, no duplicate alert).
    stream.Publish(first);
    clock.Now = T0.AddMinutes(20);
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15)));
  }

  [Fact]
  public void Started_Beats_TheFreshSupervisor()
  {
    (FakeStream stream, ChildSupervisor supervisor, AgentId child, StubClock clock) = Fresh();

    // Publish the start 10 minutes in: a beat resets the window to THAT instant, so
    // the timings below distinguish beat from no-beat (at T0 they would not — the
    // window already starts at T0 via OnStart).
    clock.Now = T0.AddMinutes(10);
    stream.Publish(new ChildStartedEvent(child, clock.Now, null, "m/sub", 2));

    clock.Now = T0.AddMinutes(20);
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15))); // beat: only 10 min silent

    clock.Now = T0.AddMinutes(26);
    Assert.NotNull(supervisor.CheckIdle(TimeSpan.FromMinutes(15))); // 16 min silent: idle now
  }

  [Fact]
  public void EveryKind_ForAnUnregisteredChild_IsANoOp_NotAFault()
  {
    FakeStream stream = new();
    ChildSupervisorRegistry registry = new();
    AgentId stranger = new(Guid.NewGuid());
    // Named decision (CA2000): test-local stream, lease outlives the publishes below.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    IDisposable lease = stream.Subscribe(new SupervisorFeed(registry));
#pragma warning restore CA2000

    Exception? ex = Record.Exception(() =>
    {
      stream.Publish(new ChildStartedEvent(stranger, T0, null, "m/sub", 1));
      stream.Publish(new ChildProgressEvent(stranger, T0, ChildPhase.ModelCall, "iteration"));
      stream.Publish(new ChildBudgetAlertEvent(stranger, T0, "tokens", 1, 10, 0));
      stream.Publish(new PreemptedEvent(stranger, T0, "agent:" + Guid.NewGuid(), 2));
      stream.Publish(new MessageDeliveredEvent(stranger, T0, "inbound", 0, 8));
      stream.Publish(new ChildIdleAlertEvent(stranger, T0, TimeSpan.FromMinutes(15), "ModelCall"));
      stream.Publish(new ChildSettledEvent(stranger, T0, AgentStatus.Failed, AgentFailureReason.Hung, 0));
    });

    Assert.Null(ex); // the feed must never create facts for a child it does not own
    lease.Dispose();
  }
}
