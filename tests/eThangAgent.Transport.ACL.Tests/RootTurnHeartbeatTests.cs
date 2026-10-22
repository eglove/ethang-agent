#pragma warning disable CA1861 // The heartbeat test reads a live event list from another thread.

using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host's LIVENESS HEARTBEAT (2026-10-22) and the app's release of an
///     abandoned turn — the two halves of "a long turn does not brick the session".
/// <para>THE DEFECT (2026-10-22): the app failed a turn whose host went silent for the
///     idle window. A long tool call (an exec script, a subagent wait) emits NOTHING, so
///     a HEALTHY host working on a long script was reported as unaddressable. The app
///     then abandoned the turn while the host kept running it — and the host holds its
///     turn gate for the whole turn, so every LATER turn queued behind it and failed the
///     same way. One long script made the session permanently unusable.</para>
/// <para>THE FIX, in two halves: the host proves it is alive (a <c>progress</c> event
///     whenever the turn has been quiet), and an abandoned turn is released on the host
///     side so the next turn can run. These tests pin the HOST half; the app half lives
///     in <see cref="ChildHostRootTurnTests"/>.</para>
/// <para>Anti-tautology: the harness is the real <see cref="RootRunHost"/>
///     over the real turn handler and the real agent loop; only the model provider and
///     the store are doubles, and the provider is parked on a genuine async yield — the
///     quiet window a long tool call really produces.</para></summary>
public sealed class RootTurnHeartbeatTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task RunTurnAsync_AQuietTurn_HeartbeatsSoTheAppNeverCallsItUnaddressable()
  {
    // The producer half of the fix: a turn that has been quiet for the heartbeat
    // interval says it is alive. Without this the app's idle bound measured BUSYNESS
    // and failed healthy long turns.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(async turnToken =>
          // The quiet window a long tool call produces: nothing is emitted while the
          // model works.
          await Task.Delay(TimeSpan.FromMilliseconds(2500), turnToken).ConfigureAwait(false))));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-quiet", "one", []) { IdleTimeout = TimeSpan.FromSeconds(4) },
        events.Add, TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Contains(events, e => e.Kind == "progress");
    // The heartbeat never follows the settle: the turn's LAST word is its answer.
    Assert.Equal("settled", events[^1].Kind);
  }

  [Fact]
  public async Task RunTurnAsync_NoIdleWindow_StillCompletesTheTurn()
  {
    // A turn that names no window (a caller that does not know the app's policy) still
    // runs to its answer: the host must never be the reason a turn is judged dead. The
    // default heartbeat interval is generous (30s), so this test asserts the TURN is
    // unaffected rather than counting heartbeats.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(async turnToken =>
          await Task.Delay(TimeSpan.FromMilliseconds(300), turnToken).ConfigureAwait(false))));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-no-window", "one", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("settled", events[^1].Kind);
  }

  [Fact]
  public async Task RunTurnAsync_ACancelledTurn_ReleasesTheGate_SoTheNextTurnRuns()
  {
    // The bricking mechanism, host side: the host holds its turn gate for the whole
    // turn. A turn that was STOPPED (or abandoned) must release that gate, or every
    // later turn queues behind it and the session is dead.
    using ManualResetEventSlim release = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Park: new ScriptedPark(async turnToken =>
          // The park observes the TURN's token: that is how an interrupted turn ends,
          // exactly as a model call or a tool that honours cancellation does.
          await Task.Run(() => release.Wait(TimeSpan.FromSeconds(20), turnToken),
              TestContext.Current.CancellationToken).ConfigureAwait(false))));
    using CancellationTokenSource stopped = new();

    Task<Result<string>> first = host.RunTurnAsync(
        new RootTurnRequest("turn-one", "one", []), _ => { }, stopped.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    await stopped.CancelAsync().ConfigureAwait(true);
    Result<string> firstResult = await first.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(firstResult.IsSuccess);
    Assert.Equal("TurnCancelled", firstResult.Error.Code);

    // The gate is free: the next turn runs and settles instead of queueing forever.
    release.Set();
    Result<string> second = await host.RunTurnAsync(
        new RootTurnRequest("turn-two", "two", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(second.IsSuccess, second.Error?.Message);
  }
}
