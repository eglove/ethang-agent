using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>W4.4: the unread badge is PUSHED its updates - a subscriber on the
///     session's child-event stream raises the count on MessageDelivered and clears
///     it on drain and settle. No timer ever samples the mailboxes (doctrine: no new
///     polling). Foreign ids never move the badge.</summary>
public class UnreadBadgeTests
{
  private sealed class CaptureStream : IAgentEvents
  {
    private readonly List<IAgentEventSubscriber> _subscribers = [];
    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      _subscribers.Add(subscriber);
      return new Lease();
    }

    public void Publish(ChildEvent evt)
    {
      foreach (IAgentEventSubscriber s in _subscribers.ToArray())
      {
        s.OnEvent(evt);
      }
    }

    private sealed class Lease : IDisposable
    {
      public void Dispose() { }
    }
  }

  private static (CaptureStream Stream, UnreadBadgeSubscriber Subscriber) Make()
  {
    CaptureStream stream = new();
    UnreadBadgeSubscriber subscriber = new(new AgentId(Guid.NewGuid()), count => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000
    return (stream, subscriber);
  }

  [Fact]
  public void Delivered_IncrementsTheCount()
  {
    (CaptureStream stream, UnreadBadgeSubscriber subscriber) = Make();
    AgentId child = new(Guid.NewGuid());
    subscriber = new UnreadBadgeSubscriber(child, _ => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000

    stream.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 8));

    Assert.Equal(1, subscriber.Count);
  }

  [Fact]
  public void Drain_ClearsTheCount()
  {
    (CaptureStream stream, UnreadBadgeSubscriber subscriber) = Make();
    AgentId child = new(Guid.NewGuid());
    subscriber = new UnreadBadgeSubscriber(child, _ => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000
    stream.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 8));
    stream.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 8));
    Assert.Equal(2, subscriber.Count);

    stream.Publish(new MailboxDrainedEvent(child, DateTimeOffset.UtcNow, [Guid.NewGuid(), Guid.NewGuid()]));

    Assert.Equal(0, subscriber.Count);
  }

  [Fact]
  public void Settle_ClearsTheCount()
  {
    (CaptureStream stream, UnreadBadgeSubscriber subscriber) = Make();
    AgentId child = new(Guid.NewGuid());
    subscriber = new UnreadBadgeSubscriber(child, _ => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000
    stream.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "inbound", 8));

    stream.Publish(new ChildSettledEvent(child, DateTimeOffset.UtcNow, AgentStatus.Completed, null, 4));

    Assert.Equal(0, subscriber.Count);
  }

  [Fact]
  public void ForeignIds_NeverMoveTheBadge()
  {
    (CaptureStream stream, UnreadBadgeSubscriber subscriber) = Make();
    subscriber = new UnreadBadgeSubscriber(new AgentId(Guid.NewGuid()), _ => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000

    stream.Publish(new MessageDeliveredEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, "inbound", 8));
    stream.Publish(new MailboxDrainedEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, [Guid.NewGuid()]));

    Assert.Equal(0, subscriber.Count);
  }

  /// <summary>Issue #316 D6: with agent-to-agent messaging retired, the HUMAN is the only
  ///     sender left, so the badge means "steering messages this session sent that the agent
  ///     has not read yet". The root steer acceptance path publishes this direction, and it
  ///     must raise the count - without it the badge would have no increment source at all,
  ///     which is the working affordance D6 refuses to trade for tidiness.</summary>
  [Fact]
  public void RootSteerDirection_IncrementsToo()
  {
    // The direction is metadata, not a different fact: the badge counts a root-steer
    // delivery exactly as it counted an in-session one.
    (CaptureStream stream, UnreadBadgeSubscriber subscriber) = Make();
    AgentId child = new(Guid.NewGuid());
    subscriber = new UnreadBadgeSubscriber(child, _ => { });
    // Named decision (CA2000): the lease lives for the test method's duration - the
    // stream is test-local and dies with it, so no disposal ordering can arise.
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
    _ = stream.Subscribe(subscriber);
#pragma warning restore CA2000

    stream.Publish(new MessageDeliveredEvent(child, DateTimeOffset.UtcNow, "root-steer", 8));

    Assert.Equal(1, subscriber.Count);
  }
}
