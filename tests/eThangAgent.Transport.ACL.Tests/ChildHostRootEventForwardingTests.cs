using eThangAgent.AgentDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The ROOT container's event forwarder must attach the moment the runner exists
///     (issue #8 follow-up). It used to attach only from the root-steer path, so a host
///     nobody steered forwarded NOTHING - every plan-change fact the root loop published
///     died in the host process, and the app's Plans view had only its poller to fall back
///     on. That is why the list stayed stale until the user switched workbench views.
///     <para>The property under test is the SUBSCRIPTION, not the transport: a rig with no
///     app attached drops wire writes by contract, so the assertion is that the root
///     container's stream has a subscriber at all.</para></summary>
public class ChildHostRootEventForwardingTests
{
  [Fact]
  public void AttachingTheRootRunner_SubscribesTheEventForwarder_WithoutAnySteer()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("answer", ["a"]);

    // AttachRootRun ran inside Start. The runner's own container stream is what the
    // forwarder subscribes, so a subscriber present there is the fact under test.
    IAgentEvents events = host.Runner.Services.GetRequiredService<IAgentEvents>();
    long before = host.ForwardedEventCount;

    // Publish a plan-change fact the way the composition's PlanChangePublisher does.
    // With the forwarder attached, the server observes it and attempts a wire send.
    events.Publish(new PlanChangedEvent(AgentId.NewId(), DateTimeOffset.UtcNow, 42, "status", "Abandoned"));

    Assert.True(host.ForwardedEventCount > before,
        "the root container's events must reach the host's forwarder without any steer");
  }
}
