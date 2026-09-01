using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The supervisor registry feed (handoff item 2, shared seam): a child's
///     progress events must reach its supervisor (beat + phase), or the idle clock
///     never resets and every healthy child false-positives as hung once it outlives
///     the idle threshold. These tests pin the feed; the host-side watchdog (ChildHost)
///     uses the same feed so remote children get idle detection from the same facts.
/// </summary>
public class SupervisorFeedTests
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

  [Fact]
  public void Progress_Event_Resets_The_Supervisor_Idle_Clock()
  {
    FakeStream stream = new();
    ChildSupervisorRegistry registry = new();
    StubClock clock = new(T0);
    AgentId child = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    registry.Register(child, supervisor);
    using IDisposable lease = stream.Subscribe(new SupervisorFeed(registry));

    clock.Now = T0.AddMinutes(10);
    stream.Publish(new ChildProgressEvent(child, clock.Now, ChildPhase.ToolExec, "tool:exec"));

    clock.Now = T0.AddMinutes(16);
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15))); // beat reset the window
    clock.Now = T0.AddMinutes(26);
    ChildIdleAlertEvent? late = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(late); // 26 minutes silent: idle now
  }

  [Fact]
  public void Unrelated_Child_Events_Do_Not_Feed_The_Wrong_Supervisor()
  {
    FakeStream stream = new();
    ChildSupervisorRegistry registry = new();
    StubClock clock = new(T0);
    AgentId child = new(Guid.NewGuid());
    AgentId other = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    registry.Register(child, supervisor);
    using IDisposable lease = stream.Subscribe(new SupervisorFeed(registry));

    stream.Publish(new ChildProgressEvent(other, T0.AddMinutes(1), ChildPhase.ToolExec, "tool:x"));

    clock.Now = T0.AddMinutes(16);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert); // another child's beat must not refresh THIS supervisor
  }

  [Fact]
  public void Settled_Child_Is_Forgotten_By_The_Feed()
  {
    FakeStream stream = new();
    ChildSupervisorRegistry registry = new();
    StubClock clock = new(T0);
    AgentId child = new(Guid.NewGuid());
    ChildSupervisor supervisor = new(child, stream, clock, ceilings: null);
    supervisor.OnStart(0);
    registry.Register(child, supervisor);
    using IDisposable lease = stream.Subscribe(new SupervisorFeed(registry));

    stream.Publish(new ChildSettledEvent(child, T0.AddMinutes(1), AgentStatus.Completed, null, 10));

    // The supervisor is gone from the registry after the settle event: the tick
    // iterates the registry, so a settled child can never raise an idle alert again.
    Assert.DoesNotContain(supervisor, registry.All);
  }
}
