using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Issue #253 B1: user steering must reach the ROOT conversation, which lives in
///     the host process. The app posts into its own container's inbox, and that container
///     runs no agent work at all (spec D6) - so before this, a message typed while a turn
///     ran landed in a mailbox nothing drains.
///     <para>The wire carries the steer addressed to the LIVE ROOT TURN (a root turn has no
///     record id, exactly like the interrupt path), and the host delivers it into the ROOT
///     container's inbox with the sender's message identity intact.</para></summary>
public class ChildHostRootSteerWireTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  [Fact]
  public async Task DeliverToLiveRootTurn_ReachesTheRootInbox()
  {
    BoundedAgentMailbox mailbox = new();
    using ScriptedRootHost host = ScriptedRootHost.Start(
        "answer", ["a"], options: new ScriptedOptions(Inbox: mailbox));

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

    await turnCts.CancelAsync().ConfigureAwait(true);
    _ = await turn.ConfigureAwait(true);
  }

  /// <summary>Issue #319 D3: THE IDENTITY TRAVELS END TO END. The app mints the id its
  ///     pinned echo hangs on, the wire carries it, and the loop's drain fact must name
  ///     THAT id back — a drain naming anything else releases nothing.
  ///     <para>The pre-existing test above calls <c>DeliverToRootTurn</c> with NO id, so it
  ///     proved delivery while the identity handshake was never exercised. That is exactly
  ///     why the drop shipped: the root inbox did not implement <c>IAgentMailbox</c>, so the
  ///     host's addressability test failed and every steer fell through to the minting
  ///     <c>TryPost</c> path.</para>
  ///     <para>Anti-tautology: the inbox is the REAL <c>BoundedAgentMailbox</c>, the delivery
  ///     goes through the server's own public seam, and the drain is the same
  ///     <c>TryTake</c> the loop performs. Only the model provider is scripted.</para></summary>
  [Fact]
  public async Task DeliverToLiveRootTurn_PreservesTheMessageId_SoTheDrainFactNamesIt()
  {
    BoundedAgentMailbox mailbox = new();
    using ScriptedRootHost host = ScriptedRootHost.Start(
        "answer", ["a"], options: new ScriptedOptions(Inbox: mailbox));

    using CancellationTokenSource turnCts = new();
    host.Server.RegisterRootTurn("turn-identity", turnCts);
    Task<Result<string>> turn = host.RunTurnAsync(
        new RootTurnRequest("turn-identity", "long work", []),
        _ => { },
        turnCts.Token);

    await WaitUntilAsync(() => host.ProviderCalls > 0).ConfigureAwait(true);

    // The app's steer, carrying the id its echo was pinned under.
    Guid pinned = Guid.NewGuid();
    host.Server.DeliverToRootTurn("turn-identity", "take over", "human", pinned);

    await WaitUntilAsync(() => mailbox.UnreadCount > 0).ConfigureAwait(true);
    // THE ASSERTION THE DEFECT WAS: the id the app minted is the id the inbox holds.
    Assert.True(mailbox.TryTake(out string text, out Guid drained));
    Assert.Equal("take over", text);
    Assert.Equal(pinned, drained);

    await turnCts.CancelAsync().ConfigureAwait(true);
    _ = await turn.ConfigureAwait(true);
  }

  [Fact]
  public async Task DeliverToAnUnknownRootTurn_IsDroppedSilently()
  {
    BoundedAgentMailbox mailbox = new();
    using ScriptedRootHost host = ScriptedRootHost.Start(
        "answer", ["a"], options: new ScriptedOptions(Inbox: mailbox));

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
