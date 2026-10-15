namespace eThangAgent.AgentDomain.Tests;

/// <summary>Child-event translation: wake mapping, per-child progress coalescing,
///     lease disposal. The fake stream captures the subscriber so tests drive events
///     directly.</summary>
public class ChildEventWakeAdapterTests
{
  private sealed class RecordingEvents : IAgentEvents
  {
    public IAgentEventSubscriber? Subscriber { get; private set; }

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      Subscriber = subscriber;
      return new FakeLease(this);
    }

    public void Publish(ChildEvent evt) => Subscriber?.OnEvent(evt);

    private sealed class FakeLease(RecordingEvents owner) : IDisposable
    {
      public void Dispose() => owner.Subscriber = null;
    }
  }

  private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

  private static ChildProgressEvent Progress(AgentId id, ChildPhase phase, string label)
      => new(id, Now, phase, label);

  [Fact]
  public void Progress_Change_Wakes()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(Progress(child, ChildPhase.ToolExec, "tool:read"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Progress, signal.Reason);
    Assert.Equal(child, signal.ChildId);
    Assert.Contains("progress", signal.DigestLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Progress_SamePhaseLabelRepeated_DoesNotWake()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(Progress(child, ChildPhase.ToolExec, "tool:read"));
    events.Subscriber.OnEvent(Progress(child, ChildPhase.ToolExec, "tool:read"));

    _ = Assert.Single(seen);
  }

  [Fact]
  public void Progress_ChangeAfterRepeat_WakesAgain()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(Progress(child, ChildPhase.ToolExec, "tool:read"));
    events.Subscriber.OnEvent(Progress(child, ChildPhase.ToolExec, "tool:read"));
    events.Subscriber.OnEvent(Progress(child, ChildPhase.Draining, "tool-result"));

    Assert.Equal(2, seen.Count);
  }

  [Fact]
  public void Progress_CoalescingIsPerChild()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId childA = new(Guid.NewGuid());
    AgentId childB = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(Progress(childA, ChildPhase.ToolExec, "tool:read"));
    events.Subscriber.OnEvent(Progress(childA, ChildPhase.ToolExec, "tool:read"));
    events.Subscriber.OnEvent(Progress(childB, ChildPhase.ToolExec, "tool:read"));

    Assert.Equal(2, seen.Count);
    Assert.Equal(childB, seen[1].ChildId);
  }

  [Fact]
  public void Idle_Alert_Wakes()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(new ChildIdleAlertEvent(child, Now, TimeSpan.FromMinutes(3), "ModelCall"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Idle, signal.Reason);
    Assert.Contains("idle", signal.DigestLine, StringComparison.Ordinal);
    Assert.Contains("ModelCall", signal.DigestLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Budget_Alert_Wakes()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(new ChildBudgetAlertEvent(child, Now, "tokens", 1500.0, 2000.0, 12.5));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Budget, signal.Reason);
    Assert.Contains("tokens", signal.DigestLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Mail_Delivered_Wakes()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(new MessageDeliveredEvent(child, Now, "parent", 1, 42));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Mailbox, signal.Reason);
    Assert.Contains("parent", signal.DigestLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Settled_And_Started_DoNotWake()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(new ChildSettledEvent(child, Now, AgentStatus.Completed, null, 100));
    events.Subscriber.OnEvent(new ChildStartedEvent(child, Now, null, "model-x", 1));

    Assert.Empty(seen);
  }

  [Fact]
  public void Dispose_StopsWaking()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    adapter.Dispose();
    events.Publish(Progress(child, ChildPhase.ToolExec, "tool:read")); // Publish null-guards: unsubscribed = no-op

    Assert.Empty(seen);
  }
  [Fact]
  public void StreamStalled_Wakes_WithStallDigest()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = new(Guid.NewGuid());

    events.Subscriber!.OnEvent(new ChildStreamStalledEvent(child, Now, TimeSpan.FromSeconds(130), "content"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.StreamStall, signal.Reason);
    Assert.Contains("stream-stalled", signal.DigestLine, StringComparison.Ordinal);
    Assert.Contains("130s", signal.DigestLine, StringComparison.Ordinal);
  }
}
