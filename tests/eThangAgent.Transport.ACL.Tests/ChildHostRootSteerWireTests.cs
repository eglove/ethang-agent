using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #253 B1: user steering must reach the ROOT conversation, which lives in
///     the host process. The app posts into its own container's inbox, and that container
///     runs no agent work at all (spec D6) - so before this, a message typed while a turn
///     ran landed in a mailbox nothing drains, and the root's wait never woke.
///     <para>The wire carries the steer addressed to the LIVE ROOT TURN (a root turn has no
///     record id, exactly like the interrupt path), the host delivers it into the ROOT
///     container's inbox, and the sticky user wake arms on the hub that container's waits
///     subscribe to. Both halves are asserted: the message is queued AND the wake fired.</para></summary>
public class ChildHostRootSteerWireTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  [Fact]
  public async Task DeliverToLiveRootTurn_ReachesTheRootInbox_AndArmsTheUserWake()
  {
    BoundedAgentMailbox mailbox = new();
    WaitWakeHub hub = new();
    SignalingInbox rootInbox = new(mailbox, hub, AgentId.NewId());
    using ScriptedRootHost host = ScriptedRootHost.Start(
        "answer", ["a"], options: new ScriptedOptions(Inbox: rootInbox));

    List<WakeSignal> wakes = [];
    using IDisposable lease = hub.Subscribe(wakes.Add);

    // Register the turn as LIVE the way the server's own root-turn dispatch does
    // (HandleRootTurnAsync): the delivery path refuses a turn id it does not hold, so a
    // test that skipped this would prove only that the refusal works.
    using CancellationTokenSource turnCts = new();
    host.Server.RegisterRootTurn("turn-steer", turnCts);
    Task<Result<string>> turn = host.RunTurnAsync(
        new RootTurnRequest("turn-steer", "long work", []),
        _ => { },
        turnCts.Token);

    await WaitUntilAsync(() => host.ProviderCalls > 0).ConfigureAwait(true);

    // The app-side steer, addressed to the live root turn.
    host.Server.DeliverToRootTurn("turn-steer", "the child is spinning - take over", "human");

    await WaitUntilAsync(() => mailbox.UnreadCount > 0).ConfigureAwait(true);
    Assert.True(mailbox.TryTake(out string text, out _));
    Assert.Equal("the child is spinning - take over", text);

    // The wake is the half that makes agent.wait return: the sticky user wake.
    WakeSignal signal = Assert.Single(wakes);
    Assert.Equal(WakeReason.UserMessage, signal.Reason);

    await turnCts.CancelAsync().ConfigureAwait(true);
    _ = await turn.ConfigureAwait(true);
  }

  [Fact]
  public async Task DeliverToAnUnknownRootTurn_IsDroppedSilently()
  {
    BoundedAgentMailbox mailbox = new();
    SignalingInbox rootInbox = new(mailbox, new WaitWakeHub(), AgentId.NewId());
    using ScriptedRootHost host = ScriptedRootHost.Start(
        "answer", ["a"], options: new ScriptedOptions(Inbox: rootInbox));

    host.Server.DeliverToRootTurn("no-such-turn", "ghost", "human");

    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(0, mailbox.UnreadCount);
  }

  private static async Task WaitUntilAsync(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit");
      }

      await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
