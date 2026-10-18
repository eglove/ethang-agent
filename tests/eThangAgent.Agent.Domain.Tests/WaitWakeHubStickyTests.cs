namespace eThangAgent.AgentDomain.Tests;

/// <summary>Sticky wake semantics: a signal registered sticky is delivered to current
///     subscribers immediately AND to every subscriber that subscribes while it remains
///     set; ClearSticky stops further delivery. The user-steering wake is sticky because
///     the fact 'a user message is pending delivery' persists until the inbox drains.</summary>
public class WaitWakeHubStickyTests
{
  private static WakeSignal Signal(WakeReason reason)
    => new(reason, new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, reason + " digest");

  [Fact]
  public void RegisterSticky_WakesCurrentSubscribers_Immediately()
  {
    WaitWakeHub hub = new();
    int seen = 0;
    using IDisposable lease = hub.Subscribe(_ => seen++);

    hub.RegisterSticky(Signal(WakeReason.UserMessage));

    Assert.Equal(1, seen);
  }

  [Fact]
  public void RegisterSticky_WakesSubscriberThatSubscribesAfterTheSignal()
  {
    WaitWakeHub hub = new();
    hub.RegisterSticky(Signal(WakeReason.UserMessage));
    int late = 0;

    using IDisposable lease = hub.Subscribe(_ => late++);

    Assert.Equal(1, late); // the pending user message wakes the wait that starts late
  }

  [Fact]
  public void ClearSticky_StopsDeliveryToLaterSubscribers()
  {
    WaitWakeHub hub = new();
    hub.RegisterSticky(Signal(WakeReason.UserMessage));
    hub.ClearSticky(WakeReason.UserMessage);
    int late = 0;

    using IDisposable lease = hub.Subscribe(_ => late++);

    Assert.Equal(0, late);
  }

  [Fact]
  public void Sticky_DeliversToEachSequentialSubscriber_WhileSet()
  {
    WaitWakeHub hub = new();
    hub.RegisterSticky(Signal(WakeReason.UserMessage));
    int first = 0, second = 0;

    using IDisposable _1 = hub.Subscribe(_ => first++);
    using IDisposable _2 = hub.Subscribe(_ => second++);

    Assert.Equal(1, first);
    Assert.Equal(1, second); // the message is still pending: the re-wait wakes too
  }

  [Fact]
  public void ClearSticky_DoesNotAffectOtherReasons()
  {
    WaitWakeHub hub = new();
    hub.RegisterSticky(Signal(WakeReason.UserMessage));
    hub.ClearSticky(WakeReason.Progress);
    int seen = 0;

    using IDisposable lease = hub.Subscribe(_ => seen++);

    Assert.Equal(1, seen);
  }
}
