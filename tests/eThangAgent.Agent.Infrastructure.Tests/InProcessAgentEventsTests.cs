using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Fan-out semantics: subscription order, ephemerality (no replay), and
///     subscriber-fault containment — the publishing loop must never see a fault.</summary>
public class InProcessAgentEventsTests
{
  private sealed class RecordingSubscriber(List<ChildEvent> sink, Exception? throwOnProgress = null)
      : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      if (throwOnProgress is not null && evt is ChildProgressEvent)
      {
        throw throwOnProgress;
      }

      sink.Add(evt);
    }
  }

  private static ChildStartedEvent Started(int attempts) => new(
      new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, null, "provider/model-x", attempts);

  [Fact]
  public void Publish_FansOutInSubscriptionOrder_AndIsEphemeral()
  {
    InProcessAgentEvents events = new();
    ChildStartedEvent firstEvent = Started(attempts: 1);
    ChildStartedEvent secondEvent = Started(attempts: 2);
    List<ChildEvent> first = [];
    List<ChildEvent> second = [];
    using IDisposable firstLease = events.Subscribe(new RecordingSubscriber(first));
    using IDisposable secondLease = events.Subscribe(new RecordingSubscriber(second));
    events.Publish(firstEvent);
    firstLease.Dispose();
    secondLease.Dispose();
    events.Publish(secondEvent); // both leases disposed: nobody receives this
    _ = Assert.Single(first);
    _ = Assert.Single(second);
    Assert.DoesNotContain(first, e => e.ChildId == secondEvent.ChildId);
    Assert.DoesNotContain(second, e => e.ChildId == secondEvent.ChildId);
  }

  [Fact]
  public void Publish_SubscriberFault_IsContained_NotPropagated()
  {
    List<string> faults = [];
    InProcessAgentEvents events = new(faultLog: faults.Add);
    List<ChildEvent> healthy = [];
    using IDisposable throwingLease = events.Subscribe(new RecordingSubscriber(healthy, throwOnProgress: new InvalidOperationException("boom")));
    using IDisposable healthyLease = events.Subscribe(new RecordingSubscriber(healthy));
    events.Publish(new ChildProgressEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, ChildPhase.ToolExec, "tool:x"));
    _ = Assert.Single(healthy);
    _ = Assert.Single(faults);
  }
}
