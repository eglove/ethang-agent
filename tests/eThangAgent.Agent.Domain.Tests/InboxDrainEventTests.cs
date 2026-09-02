using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>W4.4's domain half: the loop's safe-point drain publishes
///     <see cref="MailboxDrainedEvent"/> on the wired stream — the event a host's
///     unread badge clears on. Delivered-then-drained is the whole cycle: deliver
///     raises the queue (MessageDeliveredEvent), drain empties it (this pin).
///     Nothing publishes when the box was empty (no phantom clears).</summary>
public class InboxDrainEventTests
{
  private static AgentRecord Child() => AgentRecord.Spawned(AgentId.NewId(), null, 1, "m/sub",
      "drain", "start", new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc));

  /// <summary>One child record minted once per test so the drained id matches the
  ///     run's id (the spawner runs the record it is handed).</summary>
  private static (AgentRecord Child, AgentId Id) NewChild()
  {
    AgentRecord child = Child();
    return (child, child.Id);
  }

  [Fact]
  public async Task DeliveredSteering_PublishesDrainEventWithCount()
  {
    BoundedAgentMailbox mailbox = new();
    _ = mailbox.Deliver(new PendingMessage("steer: check the file", MessageUrgency.Normal,
        DateTimeOffset.UtcNow, "parent"));

    EventCapture capture = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")])),
        Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), new FakeAgentStore(),
        new ToolRegistry([new FakeTool("read", "ok")]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub"),
        Events: capture,
        InboxFor: _ => mailbox));

    (AgentRecord child, AgentId id) = NewChild();
    AgentRunOutcome outcome = await spawner.RunAsync(child, TestContext.Current.CancellationToken);

    Assert.Equal(AgentStatus.Completed, outcome.Status);
    MailboxDrainedEvent drained = Assert.Single(capture.Events.OfType<MailboxDrainedEvent>());
    Assert.Equal(1, drained.Count);
    Assert.Equal(id, drained.ChildId);
  }

  [Fact]
  public async Task EmptyBox_PublishesNoDrainEvent()
  {
    EventCapture capture = new();
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    SubAgentSpawner spawner = new(new SubAgentServices(
        new FakeModelProviderFactory(provider), new FakeAgentStore(),
        new ToolRegistry([]),
        new StaticPromptProvider("guide"),
        new SubAgentOptions(DefaultModel: "m/sub"),
        Events: capture,
        InboxFor: _ => new BoundedAgentMailbox()));

    _ = await spawner.RunAsync(Child(), TestContext.Current.CancellationToken);

    Assert.DoesNotContain(capture.Events, e => e is MailboxDrainedEvent);
  }

  private sealed class EventCapture : IAgentEvents
  {
    public List<ChildEvent> Events { get; } = [];
    public IDisposable Subscribe(IAgentEventSubscriber subscriber) => new NoLease();
    public void Publish(ChildEvent evt) => Events.Add(evt);
    private sealed class NoLease : IDisposable
    {
      public void Dispose() { }
    }
  }
}
