using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The signaling inbox's sticky-wake contract: the wake fires at post time
///     AND stays armed while a message remains pending, so a wait that starts after the
///     post still wakes; the inbox clears the sticky wake when its queue drains.</summary>
public class SignalingInboxStickyWakeTests
{
  /// <summary>An ADDRESSABLE inner box: the decorator refuses a non-addressable one
  ///     (issue #319 D2), because a decorator that cannot forward Deliver drops the
  ///     sender's message identity.</summary>
  private sealed class FakeInbox : IAgentInbox, IAgentMailbox
  {
    private readonly Queue<PendingMessage> _pending = [];
    public int Capacity { get; set; } = int.MaxValue;

    public int UnreadCount => _pending.Count;

    public Result<bool> TryPost(string text)
        => Deliver(PendingMessage.New(text, "human"));

    public Result<bool> Deliver(PendingMessage message)
    {
      if (_pending.Count >= Capacity)
      {
        return Result.Failure<bool>(new DomainError("MailboxFull", "full"));
      }

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

  /// <summary>Issue #189 B2: a refused post wakes nobody and leaves no sticky wake, so
  ///     a later wait does not return immediately for a message that was never queued.</summary>
  [Fact]
  public void RefusedPost_NoStickyWake_Armed()
  {
    FakeInbox inner = new() { Capacity = 0 };
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));

    Result<bool> refused = inbox.TryPost("never queued");
    Assert.False(refused.IsSuccess);

    int late = 0;
    using IDisposable lease = hub.Subscribe(_ => late++);
    Assert.Equal(0, late); // no sticky wake: the later wait sleeps
  }

  /// <summary>Issue #189 B3: an accepted post still arms the wake, and taking the last
  ///     message still clears it - unchanged behavior, pinned beside the refusal case.</summary>
  [Fact]
  public void AcceptedPost_StillArms_AndTakingClears()
  {
    FakeInbox inner = new();
    WaitWakeHub hub = new();
    SignalingInbox inbox = new(inner, hub, new AgentId(Guid.NewGuid()));

    Result<bool> accepted = inbox.TryPost("queued");
    Assert.True(accepted.IsSuccess);
    int late = 0;
    using (IDisposable lease = hub.Subscribe(_ => late++))
    {
      Assert.Equal(1, late); // armed
    }

    _ = inbox.TryTake(out string _, out Guid _);
    int late2 = 0;
    using IDisposable lease2 = hub.Subscribe(_ => late2++);
    Assert.Equal(0, late2); // cleared
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
    _ = inbox.TryTake(out string _, out Guid _);
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
    _ = inbox.TryTake(out string _, out Guid _);
    int late = 0;

    using IDisposable lease = hub.Subscribe(_ => late++);
    Assert.Equal(1, late);

    _ = inbox.TryTake(out string _, out Guid _);
    int late2 = 0;
    using IDisposable lease2 = hub.Subscribe(_ => late2++);
    Assert.Equal(0, late2);
  }
}
