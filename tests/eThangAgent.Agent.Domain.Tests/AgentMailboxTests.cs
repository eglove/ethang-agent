using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Mailbox semantics: FIFO across senders, bounded overflow failing the sender
///     with MailboxFull, NotRunning after Close, and the legacy IAgentInbox adapter shape.</summary>
public class AgentMailboxTests
{
  private static PendingMessage Message(string text, string sender = "parent",
      MessageUrgency urgency = MessageUrgency.Normal)
      => PendingMessage.New(text, urgency, sender);

  [Fact]
  public void Drain_IsFifo_AcrossSenders()
  {
    BoundedAgentMailbox box = new(capacity: 8);
    Assert.True(box.Deliver(Message("a")).IsSuccess);
    Assert.True(box.Deliver(Message("b", sender: "sibling", urgency: MessageUrgency.Attention)).IsSuccess);
    Assert.True(box.Deliver(Message("c")).IsSuccess);

    IReadOnlyList<PendingMessage> drained = box.Drain();

    Assert.Equal(["a", "b", "c"], [.. drained.Select(m => m.Text)]);
    Assert.Equal(0, box.UnreadCount);
    Assert.Empty(box.Drain());
  }

  [Fact]
  public void Deliver_PastCapacity_FailsSenderWithMailboxFull_NeverDropsSilently()
  {
    BoundedAgentMailbox box = new(capacity: 2);
    Assert.True(box.Deliver(Message("1")).IsSuccess);
    Assert.True(box.Deliver(Message("2")).IsSuccess);

    Result<bool> overflow = box.Deliver(Message("3"));

    Assert.False(overflow.IsSuccess);
    Assert.Equal(MailboxErrors.Full, overflow.Error.Code);
    Assert.Equal(2, box.UnreadCount); // the queued two are untouched
  }

  [Fact]
  public void Deliver_AfterClose_FailsNotRunning()
  {
    BoundedAgentMailbox box = new(capacity: 4);
    box.Close();

    Result<bool> refused = box.Deliver(Message("late"));

    Assert.False(refused.IsSuccess);
    Assert.Equal(MailboxErrors.NotRunning, refused.Error.Code);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void Constructor_NonPositiveCapacity_Throws(int capacity)
      => _ = Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedAgentMailbox(capacity));

  [Fact]
  public void Post_TheLegacySeque_EnqueuesAsNormalFromHuman()
  {
    BoundedAgentMailbox box = new(capacity: 4);
    box.Post("steer now");

    IReadOnlyList<PendingMessage> drained = box.Drain();
    _ = Assert.Single(drained);
    Assert.Equal("steer now", drained[0].Text);
    Assert.Equal(MessageUrgency.Normal, drained[0].Urgency);
    Assert.Equal("human", drained[0].Sender);
  }

  /// <summary>Issue #189 B1: the reportable post. A full mailbox returns MailboxFull
  ///     to the caller instead of discarding the message.</summary>
  [Fact]
  public void TryPost_PastCapacity_FailsWithMailboxFull()
  {
    BoundedAgentMailbox box = new(capacity: 2);
    Assert.True(box.TryPost("1").IsSuccess);
    Assert.True(box.TryPost("2").IsSuccess);

    Result<bool> overflow = box.TryPost("3");

    Assert.False(overflow.IsSuccess);
    Assert.Equal(MailboxErrors.Full, overflow.Error.Code);
    Assert.Equal(2, box.UnreadCount); // nothing queued
  }

  /// <summary>Issue #189: a closed mailbox's refusal is visible to the human caller too.</summary>
  [Fact]
  public void TryPost_AfterClose_FailsWithNotRunning()
  {
    BoundedAgentMailbox box = new(capacity: 4);
    box.Close();

    Result<bool> refused = box.TryPost("late");

    Assert.False(refused.IsSuccess);
    Assert.Equal(MailboxErrors.NotRunning, refused.Error.Code);
  }

  /// <summary>The void Post stays a working wrapper over TryPost for loop-facing callers.</summary>
  [Fact]
  public void Post_StillQueues_AsTheTryPostWrapper()
  {
    BoundedAgentMailbox box = new(capacity: 4);
    box.Post("steer now");

    Assert.Equal(1, box.UnreadCount);
  }

  [Fact]
  public void TryTake_DrainsOneAtATime_InOrder()
  {
    BoundedAgentMailbox box = new(capacity: 4);
    box.Post("first");
    box.Post("second");

    Assert.True(box.TryTake(out string first, out Guid _));
    Assert.Equal("first", first);
    Assert.True(box.TryTake(out string second, out Guid _));
    Assert.Equal("second", second);
    Assert.False(box.TryTake(out string _, out Guid _));
  }
}
