using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #92 end to end through the real loop: the handshake is only worth
///     anything if the facts it reads are the ones a real run actually publishes. These
///     tests drive a child through SubAgentSpawner with a real BoundedAgentMailbox, so the
///     drain is the loop's own safe-point drain and the remainder is the runtime's
///     settle path — not a hand-published event.</summary>
public class MessageReceiptLifecycleTests
{
  private static AgentRecord Child() => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub",
      "receipts", "start", new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc));

  private sealed class EventCapture(MessageReceiptLedger ledger) : IAgentEvents
  {
    public List<ChildEvent> Events { get; } = [];

    public IDisposable Subscribe(IAgentEventSubscriber subscriber) => new NoLease();

    /// <summary>Publishes to the capture AND the ledger, exactly as the container's stream
    ///     fans out to both — so the assertions read the same facts production would.</summary>
    public void Publish(ChildEvent evt)
    {
      Events.Add(evt);
      ledger.OnEvent(evt);
    }

    private sealed class NoLease : IDisposable
    {
      public void Dispose() { }
    }
  }

  /// <summary>A message delivered before the run starts is drained at the loop's entry
  ///     safe point, so the ledger must report RECEIVED — the fact agent.send's receipt
  ///     could never establish before this issue.</summary>
  [Fact]
  public async Task SteeringDrainedByTheLoop_ReportsReceived()
  {
    AgentRecord child = Child();
    BoundedAgentMailbox mailbox = new();
    PendingMessage steering = PendingMessage.New("check the file", MessageUrgency.Normal, "parent");
    _ = mailbox.Deliver(steering);

    MessageReceiptLedger ledger = new();
    EventCapture capture = new(ledger);
    capture.Publish(new MessageDeliveredEvent(child.Id, DateTimeOffset.UtcNow, "inbound",
        (int)steering.Urgency, System.Text.Encoding.UTF8.GetByteCount(steering.Text), steering.Id));

    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), new FakeAgentStore(),
        new ToolRegistry([]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub"),
        Events: capture,
        InboxFor: _ => mailbox));

    AgentRunOutcome outcome = await spawner.RunAsync(child, TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    MessageReceipt receipt = ledger.Find(child.Id, steering.Id)!;
    Assert.Equal(MessageReceiptState.Received, receipt.State);
    // The drain fact named exactly the message the sender was told about.
    MailboxDrainedEvent drained = Assert.Single(capture.Events.OfType<MailboxDrainedEvent>());
    Assert.Equal([steering.Id], drained.MessageIds);
  }

  /// <summary>A run that finishes without ever draining leaves the message queued; the
  ///     runtime's settle path must publish the remainder so the ledger reports
  ///     UNDELIVERED rather than silently reading as delivered.</summary>
  [Fact]
  public async Task SteeringNeverDrained_ReportsUndeliveredAtSettle()
  {
    AgentRecord child = Child();
    MessageReceiptLedger ledger = new();
    EventCapture capture = new(ledger);
    PendingMessage steering = PendingMessage.New("never seen", MessageUrgency.Normal, "parent");
    // The child's inbox is a DIFFERENT box: the message sits in the one the runtime owns
    // and the loop never touches, which is exactly the undrained case.
    BoundedAgentMailbox runtimeOwned = new();
    _ = runtimeOwned.Deliver(steering);
    capture.Publish(new MessageDeliveredEvent(child.Id, DateTimeOffset.UtcNow, "inbound",
        (int)steering.Urgency, System.Text.Encoding.UTF8.GetByteCount(steering.Text), steering.Id));

    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), new FakeAgentStore(),
        new ToolRegistry([]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub"),
        Events: capture,
        InboxFor: _ => new BoundedAgentMailbox()));

    _ = await spawner.RunAsync(child, TestContext.Current.CancellationToken);

    // The loop drained its own (empty) box: no drain fact, and the message is still queued.
    Assert.DoesNotContain(capture.Events, e => e is MailboxDrainedEvent);
    Assert.Equal(MessageReceiptState.Queued, ledger.Find(child.Id, steering.Id)!.State);

    // The runtime's settle path publishes the remainder for the box it owns.
    runtimeOwned.Close();
    IReadOnlyList<PendingMessage> remainder = runtimeOwned.Drain();
    capture.Publish(new MailboxUndeliveredEvent(child.Id, DateTimeOffset.UtcNow, [.. remainder.Select(m => m.Id)]));

    Assert.Equal(MessageReceiptState.Undelivered, ledger.Find(child.Id, steering.Id)!.State);
  }

  /// <summary>A received message never regresses: the settle remainder cannot un-deliver
  ///     a message the loop already appended to its conversation.</summary>
  [Fact]
  public void ReceivedMessage_IsNotRegressedByALaterRemainder()
  {
    MessageReceiptLedger ledger = new();
    AgentId child = AgentId.NewId();
    PendingMessage message = PendingMessage.New("steer", MessageUrgency.Normal, "parent");
    ledger.OnEvent(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 0, 5, message.Id));
    ledger.OnEvent(new MailboxDrainedEvent(child, DateTimeOffset.UtcNow, [message.Id]));

    ledger.OnEvent(new MailboxUndeliveredEvent(child, DateTimeOffset.UtcNow, [message.Id]));

    Assert.Equal(MessageReceiptState.Received, ledger.Find(child, message.Id)!.State);
  }
}
