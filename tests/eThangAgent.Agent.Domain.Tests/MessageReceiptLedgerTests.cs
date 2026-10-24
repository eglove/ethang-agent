namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #92: the receipt ledger turns the mailbox lifecycle events into
///     per-message delivery facts, so a sender can tell a message the child RECEIVED
///     (drained into its conversation at a safe point) from one that is still queued,
///     and from one the run settled without ever draining. It never invents a state
///     for an id it did not see delivered.</summary>
public class MessageReceiptLedgerTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

  private static PendingMessage Message(string text = "steer") =>
      new(text, MessageUrgency.Normal, T0, "parent", Guid.NewGuid());

  [Fact]
  public void Delivered_Message_IsQueued_NotYetReceived()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage message = Message();

    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 5, message.Id));

    MessageReceipt? receipt = ledger.Find(child, message.Id);
    Assert.NotNull(receipt);
    Assert.Equal(MessageReceiptState.Queued, receipt.State);
    Assert.Equal(child, receipt.ChildId);
    Assert.Equal(message.Id, receipt.MessageId);
  }

  [Fact]
  public void Drained_Message_BecomesReceived()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage message = Message();
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 5, message.Id));

    ledger.OnEvent(new MailboxDrainedEvent(child, T0.AddSeconds(3), [message.Id]));

    MessageReceipt? receipt = ledger.Find(child, message.Id);
    Assert.NotNull(receipt);
    Assert.Equal(MessageReceiptState.Received, receipt.State);
    Assert.Equal(T0.AddSeconds(3), receipt.At);
  }

  [Fact]
  public void Undelivered_AtSettle_BecomesUndelivered_NotReceived()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage message = Message();
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 5, message.Id));

    ledger.OnEvent(new MailboxUndeliveredEvent(child, T0.AddSeconds(9), [message.Id]));

    MessageReceipt? receipt = ledger.Find(child, message.Id);
    Assert.NotNull(receipt);
    Assert.Equal(MessageReceiptState.Undelivered, receipt.State);
  }

  [Fact]
  public void Drained_ThenSettled_StaysReceived()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage message = Message();
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 5, message.Id));
    ledger.OnEvent(new MailboxDrainedEvent(child, T0.AddSeconds(3), [message.Id]));

    ledger.OnEvent(new ChildSettledEvent(child, T0.AddSeconds(9), AgentStatus.Completed, null, 12));

    Assert.Equal(MessageReceiptState.Received, ledger.Find(child, message.Id)!.State);
  }

  [Fact]
  public void UnknownMessage_HasNoReceipt()
  {
    MessageReceiptLedger ledger = new();

    Assert.Null(ledger.Find(AgentId.NewId(), Guid.NewGuid()));
  }

  [Fact]
  public void Drain_OfAnUnseenMessage_InventsNoReceipt()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    Guid stranger = Guid.NewGuid();

    ledger.OnEvent(new MailboxDrainedEvent(child, T0, [stranger]));

    Assert.Null(ledger.Find(child, stranger));
  }

  [Fact]
  public void Messages_AreTrackedIndependently()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage received = Message("a");
    PendingMessage queued = Message("b");
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 1, received.Id));
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 1, queued.Id));

    ledger.OnEvent(new MailboxDrainedEvent(child, T0.AddSeconds(1), [received.Id]));

    Assert.Equal(MessageReceiptState.Received, ledger.Find(child, received.Id)!.State);
    Assert.Equal(MessageReceiptState.Queued, ledger.Find(child, queued.Id)!.State);
  }

  [Fact]
  public void ForChild_ListsThatChildsReceipts_OldestFirst()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    AgentId other = AgentId.NewId();
    PendingMessage first = Message("first");
    PendingMessage second = Message("second");
    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 5, first.Id));
    ledger.OnEvent(new MessageDeliveredEvent(other, T0, "inbound", 0, 5, Message().Id));
    ledger.OnEvent(new MessageDeliveredEvent(child, T0.AddSeconds(2), "inbound", 0, 6, second.Id));

    IReadOnlyList<MessageReceipt> receipts = ledger.ForChild(child);

    Assert.Equal([first.Id, second.Id], [.. receipts.Select(r => r.MessageId)]);
  }

  [Fact]
  public void ForChild_UnknownChild_IsEmpty()
  {
    MessageReceiptLedger ledger = new();

    Assert.Empty(ledger.ForChild(AgentId.NewId()));
  }

  [Fact]
  public void Ledger_IsBounded_DroppingOldestFirst()
  {
    MessageReceiptLedger ledger = new(capacity: 2);
    AgentId child = AgentId.NewId();
    PendingMessage first = Message("first");
    PendingMessage second = Message("second");
    PendingMessage third = Message("third");

    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "inbound", 0, 1, first.Id));
    ledger.OnEvent(new MessageDeliveredEvent(child, T0.AddSeconds(1), "inbound", 0, 1, second.Id));
    ledger.OnEvent(new MessageDeliveredEvent(child, T0.AddSeconds(2), "inbound", 0, 1, third.Id));

    Assert.Null(ledger.Find(child, first.Id));
    Assert.NotNull(ledger.Find(child, second.Id));
    Assert.NotNull(ledger.Find(child, third.Id));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void Constructor_NonPositiveCapacity_Throws(int capacity)
      => _ = Assert.Throws<ArgumentOutOfRangeException>(() => new MessageReceiptLedger(capacity));
}
