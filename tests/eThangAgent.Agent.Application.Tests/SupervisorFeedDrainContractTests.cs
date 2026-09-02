using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

public partial class SupervisorFeedContractTests
{
  /// <summary>W4.4: the drain event joins the feed's already-decided mailbox-lifecycle
  ///     class — NO beat. A drain clears a mailbox; it is not run progress, and
  ///     treating it as one would keep a drained-but-stalled child looking alive.
  ///     Pinned here so the classification cannot silently flip.</summary>
  [Fact]
  public void MailboxDrained_DoesNotBeat()
  {
    (FakeStream stream, ChildSupervisor supervisor, AgentId child, StubClock clock) = Fresh();

    stream.Publish(new MailboxDrainedEvent(child, T0, 3));

    clock.Now = T0.AddMinutes(15).AddTicks(1);
    ChildIdleAlertEvent? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15));
    Assert.NotNull(alert); // no beat: mailbox lifecycle is not run progress
  }
}
