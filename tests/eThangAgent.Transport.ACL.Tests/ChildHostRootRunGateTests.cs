using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Fix round 3: the defects round 2 introduced. A cancelled turn must still settle,
///     the seed refusal must apply only where the seed is actually used, an abandoned
///     hydration must never mutate the conversation, and the turn gate must be pinned by a
///     test that fails without it.</summary>
public sealed class ChildHostRootRunGateTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task RunTurnAsync_CancelledDuringHydration_StillSettles()
  {
    // Finding 1: the cancellation catch returned TurnCancelled WITHOUT emitting a settle,
    // contradicting the class's own contract. The app reads the outcome from the settled
    // event, so silence makes it wait out its whole root-turn budget on every interrupt.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700));
    List<RootTurnEvent> events = [];
    using CancellationTokenSource cancelled = new();

    Task<Result<string>> pending = host.RunTurnAsync(
        new RootTurnRequest("turn-cancel", "one", []), events.Add, cancelled.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    await cancelled.CancelAsync().ConfigureAwait(true);
    Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnCancelled", result.Error.Code);
    // THE POINT: a settle was emitted, carrying the typed cancellation.
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("TurnCancelled", settle.Payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunTurnAsync_LongSeedWithAStoreTranscript_IsNotRefused()
  {
    // Finding 2: the length check ran BEFORE the store read, so a long seed refused a turn
    // whose seed would never be consulted - the store is authoritative. The refusal belongs
    // in the seed-fallback path.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-long", "next", ["s1", "s2", "s3", "s4"]), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(["stored q", "stored a", "next"], host.Provider.LastRequestTexts);
  }

  [Fact]
  public async Task RunTurnAsync_LongSeedWithNoStoreTranscript_IsStillRefused()
  {
    // The refusal must still fire where the seed IS the history.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-long", "next", ["s1", "s2", "s3", "s4"]), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSeed", result.Error.Code);
    Assert.Equal(0, host.Provider.Calls);
  }

  [Fact]
  public async Task RunTurnAsync_CancelledHydration_DoesNotDoubleSeedTheConversation()
  {
    // Finding 3: the abandoned hydration task caught the cancellation as 'store has
    // nothing', fell through to the seed path, and seeded AFTER the latch was cleared and a
    // second turn had started its own hydration - duplicating a prefix and landing the
    // hydrated history out of order. The exact defect class finding 1 was about.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700));
    using CancellationTokenSource cancelled = new();

    Task<Result<string>> doomed = host.RunTurnAsync(
        new RootTurnRequest("turn-cancelled", "one", ["seed q", "seed a"]), _ => { },
        cancelled.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    await cancelled.CancelAsync().ConfigureAwait(true);
    _ = await doomed.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<string> recovered = await host.RunTurnAsync(
        new RootTurnRequest("turn-after", "two", []), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(recovered.IsSuccess, recovered.Error?.Message);
    // The store is authoritative: its prefix appears ONCE, the seed never appears (it was
    // never consulted), and the live turn follows.
    Assert.Equal(["stored q", "stored a", "two", "the answer"],
        host.Runner.Conversation.Messages.Select(message => message.Content));
  }

  [Fact]
  public async Task RunTurnAsync_SecondTurnWhileParked_WaitsForTheGate()
  {
    // Finding 6: the turn gate was untested - bypassing it left the suite green, because
    // the hydration single-flight alone prevents the empty-history symptom. This test parks
    // turn one BETWEEN its deltas on a genuine async yield and starts turn two inside that
    // window: without the gate the provider is called a second time while turn one is still
    // running, and the messages interleave.
    using ManualResetEventSlim parked = new(initialState: false);
    using ManualResetEventSlim release = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(async turnToken =>
        {
          parked.Set();
          await Task.Run(() =>
          {
            _ = release.Wait(TimeSpan.FromSeconds(15));
          }, TestContext.Current.CancellationToken).ConfigureAwait(false);
        })),
        storeDelay: TimeSpan.FromMilliseconds(50));
    List<RootTurnEvent> events = [];

    Task<Result<string>> first = Task.Run(() => host.RunTurnAsync(
        new RootTurnRequest("turn-one", "one", []), events.Add,
        TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => parked.IsSet, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

    // Turn one is parked mid-stream. Start turn two and give it ample time to reach the
    // provider if nothing serializes turns.
    Task<Result<string>> second = Task.Run(() => host.RunTurnAsync(
        new RootTurnRequest("turn-two", "two", []), _ => { },
        TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
    await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    int callsWhileParked = host.Provider.Calls;

    release.Set();
    _ = await first.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await second.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // Without the gate, turn two's provider call happens while turn one is parked.
    Assert.True(callsWhileParked == 1,
        "the provider was called " + callsWhileParked + " times while turn one was parked");
    // And the conversation is ordered: turn one's exchange precedes turn two's.
    IReadOnlyList<string> contents = [.. host.Runner.Conversation.Messages
        .Select(message => message.Content)];
    int one = contents.ToList().IndexOf("one");
    int two = contents.ToList().IndexOf("two");
    Assert.True(one >= 0 && two > one, "turn two's message landed before turn one's: " + one + " / " + two);
  }

  private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (!condition())
    {
      if (DateTimeOffset.UtcNow > deadline)
      {
        Assert.Fail("condition not met within the limit");
      }

      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
