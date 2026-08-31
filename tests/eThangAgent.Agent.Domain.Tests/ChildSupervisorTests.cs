using eThangAgent.ModelDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Supervisor machinery on a fake clock: idle threshold crossing exactly once
///     per episode, re-arm on beat, budget soft-threshold alert exactly once per ceiling,</summary>
public class ChildSupervisorTests
{
  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
  }

  private sealed class Collecting(List<ChildEvent> sink) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt) => sink.Add(evt);
  }

  private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
  private sealed class FakeEventStream : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new FakeLease(this, subscriber);
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber subscriber in _subscribers)
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class FakeLease(FakeEventStream owner, IAgentEventSubscriber subscriber) : IDisposable
    {
      public void Dispose() => owner._subscribers.Remove(subscriber);
    }
  }

  [Fact]
  public void Idle_ThresholdCross_RaisesAlert_WithFacts_AndOnlyOncePerEpisode()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    List<ChildEvent> seen = [];
    using IDisposable lease = events.Subscribe(new Collecting(seen));
    AgentId id = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(id, events, clock, ceilings: null);
    supervisor.OnStart(1);

    clock.Now = T0.AddMinutes(10);
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15)));

    clock.Now = T0.AddMinutes(16);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert);
    Assert.Equal(TimeSpan.FromMinutes(16), alert.IdleAge);
    _ = Assert.Single(seen);

    // Same episode: no second alert without an intervening beat.
    clock.Now = T0.AddMinutes(30);
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15)));
  }

  [Fact]
  public void Beat_ReArms_TheIdleAlert()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    List<ChildEvent> seen = [];
    using IDisposable lease = events.Subscribe(new Collecting(seen));
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);

    clock.Now = T0.AddMinutes(20);
    _ = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    supervisor.OnBeat();
    clock.Now = T0.AddMinutes(40);
    ChildIdleAlertEvent? second = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(second);
    Assert.Equal(2, seen.Count);
  }

  [Fact]
  public void Usage_CrossingSoftThreshold_RaisesExactlyOneBudgetAlert()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    List<ChildEvent> seen = [];
    using IDisposable lease = events.Subscribe(new Collecting(seen));
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock,
        new BudgetCeilings(MaxTokens: 1000));
    supervisor.OnStart(1);

    supervisor.OnUsage(new TokenUsage(300, 300)); // 600 < 800
    Assert.Empty(seen);

    supervisor.OnUsage(new TokenUsage(300, 300)); // 1200 >= 800: alert
    _ = Assert.Single(seen);

    supervisor.OnUsage(new TokenUsage(100, 100)); // already alerted
    _ = Assert.Single(seen);

    ChildBudgetAlertEvent alert = (ChildBudgetAlertEvent)seen[0];
    Assert.Equal("tokens", alert.BudgetKind);
    Assert.Equal(1200, alert.Consumed);
    Assert.Equal(1000, alert.Ceiling);
  }

  [Fact]
  public void Usage_WithoutCeilings_NeverAlerts()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    List<ChildEvent> seen = [];
    using IDisposable lease = events.Subscribe(new Collecting(seen));
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);

    supervisor.OnUsage(new TokenUsage(1_000_000, 1_000_000));
    Assert.Empty(seen);
  }
}
