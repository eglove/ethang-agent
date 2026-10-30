using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The signaling inbox decorator: forwarding stays the inner inbox's,
///     every accepted post registers one UserMessage wake on the hub.</summary>
public class SignalingInboxTests
{
  /// <summary>An ADDRESSABLE inner box: the decorator refuses a non-addressable one
  ///     (issue #319 D2), because a decorator that cannot forward Deliver drops the
  ///     sender's message identity.</summary>
  private sealed class FakeInbox : IAgentInbox, IAgentMailbox
  {
    private readonly Queue<PendingMessage> _pending = [];

    public int UnreadCount => _pending.Count;

    public Result<bool> TryPost(string text)
        => Deliver(PendingMessage.New(text, "human"));

    public Result<bool> Deliver(PendingMessage message)
    {
      _pending.Enqueue(message);
      return Result.Success(true);
    }

    public IReadOnlyList<PendingMessage> Drain()
    {
      IReadOnlyList<PendingMessage> drained = [.. _pending];
      _pending.Clear();
      return drained;
    }

    public bool TryTake(out string text, out Guid messageId)
    {
      if (_pending.Count == 0)
      {
        text = string.Empty;
        messageId = Guid.Empty;
        return false;
      }

      PendingMessage message = _pending.Dequeue();
      text = message.Text;
      messageId = message.Id;
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

    Assert.True(inner.TryTake(out string text, out Guid _));
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

    Assert.True(inbox.TryTake(out string first, out Guid _));
    Assert.True(inbox.TryTake(out string second, out Guid _));
    Assert.Equal("first", first);
    Assert.Equal("second", second);
    Assert.False(inbox.TryTake(out string _, out Guid _));
  }

  [Fact]
  public void TryTake_Empty_ReturnsFalse()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));

    Assert.False(inbox.TryTake(out string _, out Guid _));
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

  /// <summary>Issue #319 D2: the decorator must be ADDRESSABLE. The host delivers a
  ///     steering message through <see cref="IAgentMailbox.Deliver"/> when its root inbox
  ///     is one, and that is the only path that keeps the sender's identity. A decorator
  ///     that implemented only <see cref="IAgentInbox"/> made the host's addressability
  ///     test fail, so every steer fell through to the minting <c>TryPost</c> path and the
  ///     loop's drain fact named an id nothing had pinned an echo under.</summary>
  [Fact]
  public void Deliver_ForwardsTheSendersIdentity_AndTryTakeReturnsIt()
  {
    FakeInbox inner = new();
    SignalingInbox inbox = new(inner, new WaitWakeHub(), new AgentId(Guid.NewGuid()));
    Guid sent = Guid.NewGuid();

    Result<bool> delivered = inbox.Deliver(
        new PendingMessage("steer me", DateTimeOffset.UtcNow, "human", sent));

    Assert.True(delivered.IsSuccess);
    Assert.True(inbox.TryTake(out string text, out Guid drained));
    Assert.Equal("steer me", text);
    // THE ASSERTION THE DEFECT WAS: the id the sender minted is the id the drain names.
    Assert.Equal(sent, drained);
  }

  /// <summary>Issue #319 D2: an accepted Deliver arms the sticky user wake, exactly as an
  ///     accepted <c>TryPost</c> does — one path, not two.</summary>
  [Fact]
  public void Deliver_ArmsTheUserWake()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    AgentId owner = new(Guid.NewGuid());
    SignalingInbox inbox = new(inner, hub, owner);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);

    _ = inbox.Deliver(PendingMessage.New("steer me", "human"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.UserMessage, signal.Reason);
    Assert.Equal(owner, signal.ChildId);
  }

  /// <summary>Issue #319 D2: a decorator over a NON-addressable inbox is refused at
  ///     construction. Silently narrowing the contract is what produced the defect, so the
  ///     failure is loud and names the reason.</summary>
  [Fact]
  public void Construction_OverANonAddressableInbox_IsRefused()
  {
    ArgumentException error = Assert.Throws<ArgumentException>(() =>
        new SignalingInbox(new LegacyOnlyInbox(), new WaitWakeHub(), new AgentId(Guid.NewGuid())));

    Assert.Contains("IAgentMailbox", error.Message, StringComparison.Ordinal);
  }

  /// <summary>The pre-#319 shape: an inbox that can only MINT identities.</summary>
  private sealed class LegacyOnlyInbox : IAgentInbox
  {
    public Result<bool> TryPost(string text) => Result.Success(true);

    public bool TryTake(out string text, out Guid messageId)
    {
      text = string.Empty;
      messageId = Guid.Empty;
      return false;
    }
  }
}
