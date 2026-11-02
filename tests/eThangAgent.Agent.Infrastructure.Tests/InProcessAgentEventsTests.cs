using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure.Tests;

/// <summary>Fan-out semantics: subscription order, ephemerality (no replay), and
///     subscriber-fault containment — the publishing loop must never see a fault.</summary>
public class InProcessAgentEventsTests
{
  private sealed class RecordingSubscriber(List<AgentEvent> sink, Exception? throwOnProgress = null)
      : IAgentEventSubscriber
  {
    public void OnEvent(AgentEvent evt)
    {
      if (throwOnProgress is not null && evt is AgentProgressEvent)
      {
        throw throwOnProgress;
      }

      sink.Add(evt);
    }
  }

  private static AgentStartedEvent Started(int attempts) => new(
      new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, null, "provider/model-x", attempts);

  [Fact]
  public void Publish_FansOutInSubscriptionOrder_AndIsEphemeral()
  {
    InProcessAgentEvents events = new();
    AgentStartedEvent firstEvent = Started(attempts: 1);
    AgentStartedEvent secondEvent = Started(attempts: 2);
    List<AgentEvent> first = [];
    List<AgentEvent> second = [];
    using IDisposable firstLease = events.Subscribe(new RecordingSubscriber(first));
    using IDisposable secondLease = events.Subscribe(new RecordingSubscriber(second));
    events.Publish(firstEvent);
    firstLease.Dispose();
    secondLease.Dispose();
    events.Publish(secondEvent); // both leases disposed: nobody receives this
    _ = Assert.Single(first);
    _ = Assert.Single(second);
    Assert.DoesNotContain(first, e => e.Id == secondEvent.Id);
    Assert.DoesNotContain(second, e => e.Id == secondEvent.Id);
  }

  [Fact]
  public void Publish_SubscriberFault_IsContained_NotPropagated()
  {
    List<string> faults = [];
    InProcessAgentEvents events = new(faultLog: faults.Add);
    List<AgentEvent> healthy = [];
    using IDisposable throwingLease = events.Subscribe(new RecordingSubscriber(healthy, throwOnProgress: new InvalidOperationException("boom")));
    using IDisposable healthyLease = events.Subscribe(new RecordingSubscriber(healthy));
    events.Publish(new AgentProgressEvent(new AgentId(Guid.NewGuid()), DateTimeOffset.UtcNow, AgentPhase.ToolExec, "tool:x"));
    _ = Assert.Single(healthy);
    _ = Assert.Single(faults);
  }
}
