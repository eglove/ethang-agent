namespace eThangAgent.AgentDomain.Tests;

/// <summary>Wake multiplexer: fan-out semantics, lease disposal, fault containment.</summary>
public class WaitWakeHubTests
{
  private static WakeSignal Signal(WakeReason reason)
    => new(reason, new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, reason + " digest");

  [Fact]
  public void Register_WakesEveryCurrentSubscriber_ExactlyOnce()
  {
    WaitWakeHub hub = new();
    int first = 0, second = 0;
    using IDisposable _1 = hub.Subscribe(_ => first++);
    using IDisposable _2 = hub.Subscribe(_ => second++);

    hub.Register(Signal(WakeReason.UserMessage));

    Assert.Equal(1, first);
    Assert.Equal(1, second);
  }

  [Fact]
  public void Register_AfterDispose_DoesNotWakeTheGoneSubscriber()
  {
    WaitWakeHub hub = new();
    int gone = 0;
    IDisposable lease = hub.Subscribe(_ => gone++);
    lease.Dispose();

    hub.Register(Signal(WakeReason.Idle));

    Assert.Equal(0, gone);
  }

  [Fact]
  public void Register_SubscriberFault_IsContained_OthersStillNotified()
  {
    List<string> faults = [];
    WaitWakeHub hub = new(faultLog: faults.Add);
    int healthy = 0;
    using IDisposable _bad = hub.Subscribe(_ => throw new InvalidOperationException("boom"));
    using IDisposable _good = hub.Subscribe(_ => healthy++);

    hub.Register(Signal(WakeReason.StateWrite));

    Assert.Equal(1, healthy);
    string fault = Assert.Single(faults);
    Assert.Contains("boom", fault, StringComparison.Ordinal);
  }

  [Fact]
  public void Register_NoSubscribers_IsANoOp()
  {
    WaitWakeHub hub = new();

    hub.Register(Signal(WakeReason.Mailbox));

    Assert.True(true); // reached without throwing
  }
}
