using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Fix round 4: the two defects round 3 left. A refused seed must not brick the
///     runner for the rest of its life, and the abandoned-hydration guard must cover the
///     STORE path too - not just the seed path.</summary>
public sealed class ChildHostRootRunRefusalTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task RunTurnAsync_RefusedLongSeed_ThenASeedlessTurn_Succeeds()
  {
    // Finding 1: the refusal set a runner-lifetime flag and returned NORMALLY, so the
    // hydration latch stayed set and every LATER turn - seedless ones included - was
    // refused. The refusal must cost the TURN, never the runner.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);

    Result<string> refused = await host.RunTurnAsync(
        new RootTurnRequest("turn-long", "one", ["s1", "s2", "s3", "s4"]), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidSeed", refused.Error.Code);

    // THE POINT: a following SEEDLESS turn runs normally.
    List<RootTurnEvent> events = [];
    Result<string> recovered = await host.RunTurnAsync(
        new RootTurnRequest("turn-after", "two", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(recovered.IsSuccess, recovered.Error?.Message);
    Assert.Equal("the answer", recovered.Value);
    Assert.Equal("settled", events[^1].Kind);
    Assert.Equal(1, host.Provider.Calls);
  }

  [Fact]
  public async Task RunTurnAsync_RefusedLongSeed_ThenATwoLineSeedTurn_Succeeds()
  {
    // The same poisoning on the seed path: a valid two-line seed after a refusal must work.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);

    _ = await host.RunTurnAsync(
        new RootTurnRequest("turn-long", "one", ["s1", "s2", "s3", "s4"]), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<string> recovered = await host.RunTurnAsync(
        new RootTurnRequest("turn-after", "two", ["q1", "a1"]), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(recovered.IsSuccess, recovered.Error?.Message);
    Assert.Equal(["q1", "a1", "two"], host.Provider.LastRequestTexts);
  }

  [Fact]
  public async Task RunTurnAsync_CancelledHydration_TokenIgnoringStore_DoesNotDoubleSeed()
  {
    // Finding 2: the round-3 guard covered only the SEED path. A store that IGNORES the
    // cancellation token completes its read after the latch was cleared, and both the
    // orphan and the replacement hydration Seed - duplicating the store prefix.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700),
        options: new ScriptedOptions(StoreIgnoresToken: true));
    using CancellationTokenSource cancelled = new();

    Task<Result<string>> doomed = host.RunTurnAsync(
        new RootTurnRequest("turn-cancelled", "one", []), _ => { }, cancelled.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    await cancelled.CancelAsync().ConfigureAwait(true);
    _ = await doomed.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<string> recovered = await host.RunTurnAsync(
        new RootTurnRequest("turn-after", "two", []), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(recovered.IsSuccess, recovered.Error?.Message);
    // The store prefix appears exactly ONCE, then the live turn and its answer.
    Assert.Equal(["stored q", "stored a", "two", "the answer"],
        host.Runner.Conversation.Messages.Select(message => message.Content));
  }
}
