namespace eThangAgent.AgentDomain.Tests;

/// <summary>The signaling inbox decorator: forwarding stays the inner inbox's,
///     every accepted post registers one UserMessage wake on the hub.</summary>
public class SignalingInboxTests
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
  public void Post_ForwardsToInner_AndRegistersUserWake()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    AgentId owner = new(Guid.NewGuid());
    SignalingInbox inbox = new(inner, hub, owner);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);

    inbox.Post("check on the child");

    Assert.True(inner.TryTake(out string text));
    Assert.Equal("check on the child", text);
    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.UserMessage, signal.Reason);
    Assert.Equal(owner, signal.ChildId);
  }

  [Fact]
  public void TryTake_ReturnsInnerMessages_InOrder()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));

    inbox.Post("first");
    inbox.Post("second");

    Assert.True(inbox.TryTake(out string first));
    Assert.True(inbox.TryTake(out string second));
    Assert.Equal("first", first);
    Assert.Equal("second", second);
    Assert.False(inbox.TryTake(out string _));
  }

  [Fact]
  public void TryTake_Empty_ReturnsFalse()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));

    Assert.False(inbox.TryTake(out string _));
  }

  [Fact]
  public void Post_RegistersExactlyOncePerAcceptedPost()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);

    inbox.Post("one");
    inbox.Post("two");

    Assert.Equal(2, seen.Count);
  }
}
