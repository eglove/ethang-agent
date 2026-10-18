namespace eThangAgent.AgentDomain.Tests;

/// <summary>The signaling inbox's sticky-wake contract: the wake fires at post time
///     AND stays armed while a message remains pending, so a wait that starts after the
///     post still wakes; the inbox clears the sticky wake when its queue drains.</summary>
public class SignalingInboxStickyWakeTests
{
  private sealed class FakeInbox : IAgentInbox
  {
    private readonly Queue<string> _pending = [];

    public void Post(string text) => _pending.Enqueue(text);

    public bool TryTake(out string text)
    {
      if (_pending.Count == 0)
      {
        text = string.Empty;
        return false;
      }

      text = _pending.Dequeue();
      return true;
    }
  }

  [Fact]
  public void Post_KeepsStickyWakeArmed_WhileMessagePending()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));
    int late = 0;

    inbox.Post("check on the child");
    using IDisposable lease = hub.Subscribe(_ => late++);

    Assert.Equal(1, late); // the wait that starts after the post still wakes
  }

  [Fact]
  public void DrainingTheQueue_ClearsTheStickyWake()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));
    inbox.Post("one");
    _ = inbox.TryTake(out string _);
    int late = 0;

    using IDisposable lease = hub.Subscribe(_ => late++);

    Assert.Equal(0, late); // delivered: a later wait must not wake spuriously
  }

  [Fact]
  public void PartiallyDrained_KeepsStickyWakeArmed()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));
    inbox.Post("one");
    inbox.Post("two");
    _ = inbox.TryTake(out string _);
    int late = 0;

    using IDisposable lease = hub.Subscribe(_ => late++);
    Assert.Equal(1, late);

    _ = inbox.TryTake(out string _);
    int late2 = 0;
    using IDisposable lease2 = hub.Subscribe(_ => late2++);
    Assert.Equal(0, late2);
  }
}
