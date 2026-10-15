namespace eThangAgent.AgentDomain.Tests;

/// <summary>Stream-stall facts: once-per-episode alerting, re-arm on activity,
///     threshold respect, and the no-beat contract (stream activity never resets the
///     idle clock).</summary>
public class ChildSupervisorStreamStallTests
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
      foreach (IAgentEventSubscriber subscriber in _subscribers.ToArray())
      {
        subscriber.OnEvent(evt);
      }
    }

    private sealed class FakeLease(FakeEventStream owner, IAgentEventSubscriber subscriber) : IDisposable
    {
      public void Dispose() => owner._subscribers.Remove(subscriber);
    }
  }

  private static readonly DateTimeOffset T0 = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

  [Fact]
  public void OnStreamActivity_ThenThresholdCrossed_PublishesStallEventOnce()
  {
    StubClock clock = new(T0);
    List<ChildEvent> published = [];
    FakeEventStream events = new();
    using IDisposable lease = events.Subscribe(new Collecting(published));
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);
    supervisor.OnStreamActivity("reasoning");
    clock.Now = T0.AddMinutes(3);

    ChildStreamStalledEvent? first = supervisor.CheckStreamStall(TimeSpan.FromMinutes(2));
    ChildStreamStalledEvent? second = supervisor.CheckStreamStall(TimeSpan.FromMinutes(2));

    Assert.NotNull(first);
    Assert.Null(second);
    _ = Assert.Single(published); // the CheckStreamStall publish, exactly once
    Assert.Equal("reasoning", first.LastActivityKind);
  }

  [Fact]
  public void NewStreamActivity_ReArmsStall()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);
    clock.Now = T0.AddMinutes(3);
    _ = supervisor.CheckStreamStall(TimeSpan.FromMinutes(2));

    supervisor.OnStreamActivity("content");
    clock.Now = T0.AddMinutes(6);
    ChildStreamStalledEvent? again = supervisor.CheckStreamStall(TimeSpan.FromMinutes(2));

    Assert.NotNull(again);
    Assert.Equal(TimeSpan.FromMinutes(3), again.QuietFor);
    Assert.Equal("content", again.LastActivityKind);
  }

  [Fact]
  public void UnderThreshold_ReturnsNull()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);
    clock.Now = T0.AddMinutes(1);

    Assert.Null(supervisor.CheckStreamStall(TimeSpan.FromMinutes(2)));
  }

  [Fact]
  public void StreamActivity_DoesNotResetIdleClock()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);
    supervisor.OnStreamActivity("content");
    clock.Now = T0.AddMinutes(20);

    ChildIdleAlertEvent? idle = supervisor.CheckIdle(TimeSpan.FromMinutes(15));

    Assert.NotNull(idle); // stream stamps alone never refresh the idle window
  }

  [Fact]
  public void StallEvent_CarriesLastActivityKind()
  {
    StubClock clock = new(T0);
    FakeEventStream events = new();
    ChildSupervisor supervisor = new(new AgentId(Guid.NewGuid()), events, clock, ceilings: null);
    supervisor.OnStart(1);
    supervisor.OnStreamActivity("reasoning");
    clock.Now = T0.AddMinutes(5);

    ChildStreamStalledEvent? alert = supervisor.CheckStreamStall(TimeSpan.FromMinutes(2));

    Assert.Equal("reasoning", alert!.LastActivityKind);
    Assert.Equal(TimeSpan.FromMinutes(5), alert.QuietFor);
  }
}
