using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The app-side root-turn path over a real named pipe (spec D6): the app sends
///     one turn, the host streams deltas and a settle back, and the app's runtime
///     surfaces them through the caller's callback in order. A disconnected transport is
///     the typed HostUnavailable failure — never a hang — and every waiter pending at
///     connection loss is completed the same way.
///
///     Anti-tautology: the fake host writes the wire DIRECTLY (TransportFraming +
///     RootTurnProtocol) and never uses RemoteAgentRuntime, so these tests fail if the
///     production client's send, routing, or settlement is broken. The Received
///     assertions prove the request actually crossed the pipe.</summary>
public class ChildHostRootTurnTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  /// <summary>A deliberately short root-turn budget for the never-settles test: the
  ///     production default is far longer, but the CONTRACT under test is that a bound
  ///     exists and fails typed — the value itself is not the subject.</summary>
  private static readonly RootTurnBudget ShortBudget = new(
      TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));

  [Fact]
  public async Task RootTurn_StreamsDeltasThenSettles()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", "hello", ["seed-a"]), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("done", result.Value);
    Assert.Equal(["delta", "delta", "settled"], events.Select(e => e.Kind));
    Assert.Equal(["he", "llo", "done"], events.Select(e => e.Payload));
    Assert.All(events, e => Assert.Equal("turn-1", e.TurnId));

    RootTurnRequest received = Assert.Single(host.Received);
    Assert.Equal("turn-1", received.TurnId);
    Assert.Equal("hello", received.Input);
    Assert.Equal(["seed-a"], received.Seed);
  }

  [Fact]
  public async Task RootTurn_DisconnectedTransport_FailsHostUnavailable()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    await host.StopAsync().ConfigureAwait(true);
    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("HostUnavailable", result.Error.Code);
  }

  [Fact]
  public async Task RootTurn_NeverAttached_FailsHostUnavailable()
  {
    RemoteAgentRuntime runtime = new();

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("HostUnavailable", result.Error.Code);
  }

  [Fact]
  public async Task RootTurn_ConnectionLostMidTurn_CompletesTheWaiterHostUnavailable()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    // The host takes the turn and never settles it: the waiter is genuinely pending
    // when the connection dies, so completion can only come from the loss path.
    Task<Result<string>> pending = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => host.Received.Count > 0).ConfigureAwait(true);
    await host.StopAsync().ConfigureAwait(true);

    Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("HostUnavailable", result.Error.Code);
  }

  [Fact]
  public async Task RootTurn_ConnectionLostMidTurn_ThenASecondWaiter_AlsoFailsHostUnavailable()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Task<Result<string>> first = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => host.Received.Count > 0).ConfigureAwait(true);
    await host.StopAsync().ConfigureAwait(true);
    _ = await first.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The runtime is now known-dead: a fresh turn must refuse immediately, not hang
    // waiting for a host that will never answer.
    Result<string> second = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-2", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(second.IsSuccess);
    Assert.Equal("HostUnavailable", second.Error.Code);
  }

  [Fact]
  public async Task RootTurn_Cancelled_IsATypedTurnCancelledFailure()
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    using CancellationTokenSource turn = new();

    Task<Result<string>> pending = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", FakeConversationHost.SilentInput, []), _ => { }, turn.Token);
    await WaitUntilAsync(() => host.Received.Count > 0).ConfigureAwait(true);
    await turn.CancelAsync().ConfigureAwait(true);

    Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnCancelled", result.Error.Code);
  }

  [Fact]
  public async Task RootTurn_OnTheRealHostServer_AnswersWithATypedSettle()
  {
    // The host side of the protocol (task 8's ChildHostServer case) driven over a real
    // pipe: no root turn RUNNER is wired in this build (task 9 owns it), so the host
    // must answer with a typed settle failure — a well-formed outcome, never silence.
    string settings = Path.Combine(Path.GetTempPath(), "ethang-rootturn-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings, Path.Combine(Path.GetTempPath(), "ethang-rootturn-" + Guid.NewGuid().ToString("N") + ".db"));
    string pipeName = "ethang-rootturn-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    await using NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
#pragma warning restore CA2007
    server.AttachTransport(hostSide);
    // The serve loop runs until the pipe closes (its own contract) — never awaited here.
    _ = server.ServeAsync();

    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-host", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Equal("turn-host", settle.TurnId);
    Assert.Contains("RootTurnUnavailable", settle.Payload, StringComparison.Ordinal);
    Assert.Equal(settle.Payload, result.Value);
  }

  [Fact]
  public async Task RootTurn_AfterReAttach_IsAcceptedAgain()
  {
    // The lost flag refuses turns on a KNOWN-dead connection; a re-attach swaps in a
    // live one, so turns must be accepted again — otherwise one disconnect would
    // permanently disable root turns for the rest of the app's life.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost first = FakeConversationHost.Start();
    await using NamedPipeChildTransport dead = await ConnectAsync(first.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(dead);
    using CancellationTokenSource firstLoop = new();
    _ = runtime.RunReceiveLoopAsync(firstLoop.Token);

    Task<Result<string>> doomed = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => first.Received.Count > 0).ConfigureAwait(true);
    await first.StopAsync().ConfigureAwait(true);
    _ = await doomed.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    await using FakeConversationHost second = FakeConversationHost.Start();
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport fresh = await ConnectAsync(second.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    runtime.ReplaceTransport(fresh);
    using CancellationTokenSource secondLoop = new();
    _ = runtime.RunReceiveLoopAsync(secondLoop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-2", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("settled", events[^1].Kind);
    Assert.Equal("turn-2", Assert.Single(second.Received).TurnId);
  }

  [Fact]
  public async Task RootTurn_EmptyTurnId_IsRejectedWithoutSending()
  {
    // The codec refuses an empty turn id on parse; the SEND path must match, or the
    // caller's waiter is registered under a key the host can never address.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidTurnId", result.Error.Code);
    Assert.Empty(host.Received);
  }

  [Fact]
  public async Task RootTurn_DuplicateTurnId_IsRefused_AndTheFirstWaiterStillCompletes()
  {
    // Two concurrent turns sharing a TurnId must not silently orphan the first waiter:
    // the duplicate is refused with a typed error and the ORIGINAL turn still settles.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Task<Result<string>> first = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-dup", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => host.Received.Count > 0).ConfigureAwait(true);

    Result<string> duplicate = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-dup", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(duplicate.IsSuccess);
    Assert.Equal("InvalidTurnId", duplicate.Error.Code);
    // Only the first request crossed the pipe: the duplicate never reached the host.
    _ = Assert.Single(host.Received);

    // The FIRST waiter is still alive and completes when the host settles it.
    await host.ReleaseHeldTurnsAsync().ConfigureAwait(true);
    Result<string> settled = await first.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(settled.IsSuccess, settled.Error?.Message);
    Assert.Equal("released#1", settled.Value);
  }

  [Fact]
  public async Task RootTurn_EmptyTurnId_ToTheRealHost_SettlesInsteadOfHanging()
  {
    // The reviewer's end-to-end probe: a turn the host cannot address used to leave the
    // caller waiting forever. Against the REAL ChildHostServer over a real pipe the
    // caller must settle with a typed error, promptly.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync().ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest(string.Empty, "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidTurnId", result.Error.Code);
  }

  [Fact]
  public async Task RootTurn_HostNeverSettles_FailsWithATypedTimeout_NotAHang()
  {
    // The general bound behind finding 2: a host that takes the turn and never answers
    // it (an older host with no root-turn support, a dropped frame) must not strand the
    // caller. The runtime's documented turn budget settles it with a typed error.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, ShortBudget);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-silent", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnUnaddressable", result.Error.Code);
    _ = Assert.Single(host.Received);
  }

  [Fact]
  public async Task RootTurn_ThrowingCallback_DoesNotTearDownTheConnection()
  {
    // onEvent is user-supplied code running on the pump thread: a throw must not fault
    // the pump and take every other child's connection down with it.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> first = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-throw", "hello", []),
        _ => throw new InvalidOperationException("caller bug"),
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The turn still settles: the callback's fault is the caller's, not the wire's.
    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.Equal("done", first.Value);

    // And the connection is alive: a second turn round-trips normally.
    Result<string> second = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-after-throw", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal("done", second.Value);
  }

  [Fact]
  public async Task RootTurn_HealthyLongTurn_IsNotKilledByIdleBound()
  {
    // RULING 1, the false-failure case the re-reviewer proved: a HEALTHY host streaming
    // deltas every 400ms for ~2.4s must NOT be failed as unaddressable just because the
    // turn outlasts the idle window. Progress must reset the idle clock.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    // Idle window far SHORTER than the turn's total duration, but longer than the gap
    // between deltas: only a progress-blind bound could fail this.
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-slow", FakeConversationHost.SlowInput, []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("slow-done", result.Value);
    Assert.Equal(FakeConversationHost.SlowDeltaCount, events.Count(e => e.Kind == "delta"));
    Assert.Equal("settled", events[^1].Kind);
  }

  [Fact]
  public async Task RootTurn_SilentHost_FailsIdleBound()
  {
    // The idle bound's real job: a host that takes the turn and stops speaking.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-silent", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnUnaddressable", result.Error.Code);
    Assert.Contains("idle", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RootTurn_HeartbeatEvents_KeepTheIdleBoundFromFiring()
  {
    // THE FALSE POSITIVE (2026-10-22): a long tool call (an exec script, a subagent
    // wait) produces NO events, so the app read the silence as a dead host and failed a
    // HEALTHY turn - then abandoned it, and the abandoned turn held the host's turn gate
    // and bricked every later turn. The host now heartbeats: a heartbeat resets the idle
    // clock, so the bound measures LIVENESS instead of busyness.
    //
    // The window here is SHORTER than the whole turn and only a little longer than the
    // heartbeat gap: a progress-blind bound would fail this turn.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-heartbeat", FakeConversationHost.HeartbeatInput, []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("heartbeat-done", result.Value);
    Assert.Contains(events, e => e.Kind == "progress");
    Assert.Equal("settled", events[^1].Kind);
  }

  [Fact]
  public async Task RootTurn_AbandonedTurn_TellsTheHostToStopIt()
  {
    // THE BRICKING MECHANISM (2026-10-22): the app gave up on a turn the host is still
    // running, and the host holds its turn gate for the whole turn - so every LATER turn
    // queued behind it, silent, and failed the same way. That is what made the session
    // unusable from the first error onward. The app must tell the host to stop the turn
    // it is no longer waiting for.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-silent", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnUnaddressable", result.Error.Code);

    // The host was told to stop the turn the app gave up on.
    await WaitUntilAsync(() => host.InterruptedTurnIds.Contains("turn-silent")).ConfigureAwait(true);
  }

  [Fact]
  public async Task RootTurn_StoppedTurn_TellsTheHostToStopIt()
  {
    // The same release, for the caller's own stop: a stop that abandons the wait must
    // also stop the HOST, or the host keeps running (and persisting) a turn the user
    // stopped, and its turn gate stays held.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    using CancellationTokenSource turn = new();

    Task<Result<string>> pending = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-stopped", FakeConversationHost.SilentInput, []), _ => { }, turn.Token);
    await WaitUntilAsync(() => host.Received.Count > 0).ConfigureAwait(true);
    await turn.CancelAsync().ConfigureAwait(true);
    Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnCancelled", result.Error.Code);
    await WaitUntilAsync(() => host.InterruptedTurnIds.Contains("turn-stopped")).ConfigureAwait(true);
  }

  [Fact]
  public async Task RootTurn_CarriesTheIdleWindow_SoTheHostCanHeartbeatInsideIt()
  {
    // The idle window is the APP's policy, and the host is the side that has to beat it.
    // Without the number on the request the host would heartbeat on a guess - and a
    // guess that is too slow reintroduces the false failure this whole contract exists
    // to remove.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    RootTurnRequest received = Assert.Single(host.Received);
    Assert.Equal(TimeSpan.FromMilliseconds(400), received.IdleTimeout);
  }

  [Fact]
  public async Task RootTurn_ChattyHost_FailsTheHardCeiling()
  {
    // The ceiling's job: a host that keeps streaming forever never trips the IDLE bound,
    // so only a total-duration ceiling can end it. Distinct error code from the idle bound
    // so a caller can tell "stopped talking" from "would not stop".
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-chatty", FakeConversationHost.ChattyInput, []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("TurnCeilingExceeded", result.Error.Code);
    // It really was streaming: the ceiling cut a live turn, it did not merely time out.
    Assert.Contains(events, e => e.Kind == "delta");
  }

  [Fact]
  public async Task RootTurn_ReusedIdAfterTimeout_DoesNotReturnTheStalePayload()
  {
    // RULING 2, the silent cross-talk the re-reviewer proved: turn #1 on an id times out;
    // turn #2 reuses the id; turn #1's late settle then arrives. Turn #2 must NEVER
    // succeed carrying turn #1's payload.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using FakeConversationHost host = FakeConversationHost.Start();
    await using NamedPipeChildTransport app = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
        TimeSpan.FromMilliseconds(400), TimeSpan.FromMinutes(5)));
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    // Turn #1: held by the host, so it times out on the idle bound.
    Result<string> first = await runtime.SendRootTurnAsync(
        new RootTurnRequest("reused-id", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(first.IsSuccess);
    Assert.Equal("TurnUnaddressable", first.Error.Code);

    // Turn #2 on the SAME id: also held, so we can release #1's stale settle first.
    Task<Result<string>> second = runtime.SendRootTurnAsync(
        new RootTurnRequest("reused-id", FakeConversationHost.SilentInput, []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => host.Received.Count >= 2).ConfigureAwait(true);

    // The host now settles BOTH held turns in arrival order: #1's stale settle lands
    // first and must be IGNORED by turn #2.
    await host.ReleaseHeldTurnsAsync().ConfigureAwait(true);

    Result<string> secondResult = await second.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    // The ONLY acceptable outcomes: turn #2 gets its OWN payload, or it fails typed.
    // A success carrying "released#1" is the silent cross-talk this test forbids.
    if (secondResult.IsSuccess)
    {
      Assert.Equal("released#2", secondResult.Value);
    }
    else
    {
      Assert.Equal("TurnUnaddressable", secondResult.Error.Code);
    }
  }

  [Fact]
  public async Task RootTurn_NonReadingPeer_SendIsBounded_NotAHang()
  {
    // Finding 1: the SEND itself must be bounded. A peer that accepts and never reads
    // blocks a large named-pipe write indefinitely, so putting the frame on the wire must
    // not be the one unbounded step.
    //
    // Deadlock vigilance: a write blocked on a full pipe buffer cannot be interrupted by ANY
    // token, so this test is written to never leave one behind — the peer is disposed FIRST
    // in the finally, which is what unblocks the writer, and only then the app side. The
    // WhenAny race turns a regression into a clean assertion instead of a wedged testhost.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // Large enough to overflow the pipe buffer (64KB default) so the write genuinely
      // blocks, yet UNDER the transport's 4MB frame cap — a frame over the cap is rejected
      // at the seam before it can ever block, which would test the cap, not the bound.
      string bigInput = new('x', 1024 * 1024);

      Task<Result<string>> turn = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-big", bigInput, []), _ => { },
          TestContext.Current.CancellationToken);
      Task finished = await Task.WhenAny(turn, Task.Delay(Limit, TestContext.Current.CancellationToken))
          .ConfigureAwait(true);
      Assert.True(finished == turn, "the send to a non-reading peer never completed: the send path is unbounded");

      Result<string> result = await turn.ConfigureAwait(true);
      Assert.False(result.IsSuccess);
      // The contract is the CODE and that it settled at all. The message may name either
      // bound (a frame over the seam limit fails at once; a blocked write fails on idle).
      Assert.Equal("HostUnavailable", result.Error.Code);
    }
    finally
    {
      // Break the pipe first: that is the only thing that releases a blocked writer.
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }
  [Fact]
  public async Task RootTurn_BoundedSendFailure_SecondTurnIsNotBlockedOnTheGate()
  {
    // SUPERSEDED BY THE CONTROLLER RULING. This test used to assert that a bounded send
    // FAILED THE WHOLE CONNECTION (so a second turn failed fast on the lost flag). The
    // ruling established that the write is CANCELLED — which releases the gate — so the
    // connection SURVIVES and the correct assertion is that a second turn is not blocked.
    // It still fails if the write is abandoned: an abandoned write holds the gate and the
    // second turn waits out its own send window.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      Task<Result<string>> first = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-big", new string('x', 1024 * 1024), []), _ => { },
          TestContext.Current.CancellationToken);
      Task firstDone = await Task.WhenAny(first, Task.Delay(Limit, TestContext.Current.CancellationToken))
          .ConfigureAwait(true);
      Assert.True(firstDone == first, "the bounded send never returned");
      Result<string> firstResult = await first.ConfigureAwait(true);
      Assert.False(firstResult.IsSuccess);
      Assert.Equal("HostUnavailable", firstResult.Error.Code);

      // The peer drains so the pipe buffer is not the thing under test.
      peer.StartDraining();
      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);

      System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
      Task<Result<string>> second = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-small", "hello", []), _ => { },
          TestContext.Current.CancellationToken);
      Task secondDone = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      elapsed.Stop();
      Assert.True(secondDone == second,
          "the second turn blocked on the write gate: the bounded write was abandoned, not cancelled");
      // It may fail (this peer never answers a turn) — the point is that it was not BLOCKED.
      Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(900),
          "the second turn waited " + elapsed.Elapsed + ", i.e. it hit the send bound on a wedged gate");
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }
  [Fact]
  public async Task RootTurn_AfterABoundedSendFailure_ReAttachRestoresService()
  {
    // The teardown must not be terminal: a re-attach is the documented recovery, and after
    // it a turn must succeed again. Proves the connection-lost state is recoverable rather
    // than a permanent brick.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport dead = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(dead, new RootTurnBudget(
          TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10)));
      using CancellationTokenSource firstLoop = new();
      _ = runtime.RunReceiveLoopAsync(firstLoop.Token);

      Task<Result<string>> first = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-big", new string('x', 1024 * 1024), []), _ => { },
          TestContext.Current.CancellationToken);
      _ = await Task.WhenAny(first, Task.Delay(Limit, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      _ = await first.ConfigureAwait(true);

      // A fresh, healthy host on a new pipe: the re-attach path.
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using FakeConversationHost host = FakeConversationHost.Start();
      await using NamedPipeChildTransport fresh = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
      runtime.ReplaceTransport(fresh);
      using CancellationTokenSource secondLoop = new();
      _ = runtime.RunReceiveLoopAsync(secondLoop.Token);

      Result<string> recovered = await runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-after", "hello", []), _ => { },
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(recovered.IsSuccess, recovered.Error?.Message);
      Assert.Equal("done", recovered.Value);
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await dead.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_SlowReadingPeer_IsNotDiagnosedAsNotReading()
  {
    // ProbeI2: a peer that IS reading (4KB/50ms) but cannot keep up must not be reported as
    // "the host is not reading" — that is a false diagnosis of who failed.
    NonReadingPeer peer = await NonReadingPeer.StartSlowReaderAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      Task<Result<string>> turn = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-slowread", new string('x', 1024 * 1024), []), _ => { },
          TestContext.Current.CancellationToken);
      Task done = await Task.WhenAny(turn, Task.Delay(Limit, TestContext.Current.CancellationToken))
          .ConfigureAwait(true);
      Assert.True(done == turn, "the bounded send never returned");

      Result<string> result = await turn.ConfigureAwait(true);
      Assert.False(result.IsSuccess);
      Assert.Equal("HostUnavailable", result.Error.Code);
      Assert.DoesNotContain("not reading", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_CancelledDuringTheSendWindow_IsTurnCancelled_NotAHostFault()
  {
    // ProbeL: the send tick is linked to ct, so a user stop during the write used to win the
    // race and be reported as a host fault. Cancellation is the CALLER's doing and must say so.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);
      using CancellationTokenSource turn = new();

      Task<Result<string>> pending = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-cancel", new string('x', 1024 * 1024), []), _ => { }, turn.Token);
      await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
      await turn.CancelAsync().ConfigureAwait(true);

      Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(result.IsSuccess);
      Assert.Equal("TurnCancelled", result.Error.Code);
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_BoundedSendFailure_KeepsTheConnection_AndTheGateIsFree()
  {
    // CONTROLLER RULING, the CANCELLATION half. The send bound's token is passed to SendAsync,
    // so a blocked write is CANCELLED — which releases the transport's single write gate —
    // rather than abandoned. That half is why round 4 was right and round 3 was wrong.
    //
    // This is the assertion finding 4 said was missing: proof the GATE is free, not merely
    // that the lost flag short-circuits later sends. A direct send on the SAME transport is
    // the only thing that proves it — the lost-flag refusal never touches the gate.
    //
    // SCOPE, stated honestly: this peer NEVER READS, so the cancellation lands before any byte
    // of the frame reaches the wire and the stream stays intact — the recoverable case. The
    // UNRECOVERABLE case (a cancellation that interrupts a frame, which desynchronizes the peer
    // and costs the connection) is pinned separately by
    // RootTurn_CancelledMidFrame_TearsDownTheConnection_AndSettlesTheChild. Together they are
    // the ruling's two halves: cancellation frees the gate, and a mid-frame cancellation is
    // treated as connection loss.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      Task<Result<string>> turn = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-big", new string('x', 1024 * 1024), []), _ => { },
          TestContext.Current.CancellationToken);
      Task done = await Task.WhenAny(turn, Task.Delay(Limit, TestContext.Current.CancellationToken))
          .ConfigureAwait(true);
      Assert.True(done == turn, "the bounded send never returned");
      Result<string> result = await turn.ConfigureAwait(true);
      Assert.False(result.IsSuccess);
      Assert.Equal("HostUnavailable", result.Error.Code);

      // THE GATE PROOF. The peer must DRAIN first: a peer that never reads leaves the pipe
      // buffer full, so even a tiny write blocks regardless of the gate — which would make
      // this assertion measure the buffer, not the gate. With the buffer draining, a small
      // write completing proves the gate itself is free.
      peer.StartDraining();
      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Task smallSend = app.SendAsync(new TransportEnvelope("ack", "\"1\"", 1),
          TestContext.Current.CancellationToken);
      Task smallDone = await Task.WhenAny(smallSend, Task.Delay(TimeSpan.FromSeconds(5),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(smallDone == smallSend,
          "the write gate is still held after the bounded send: the abandoned write was never cancelled");
      await smallSend.ConfigureAwait(true);

      // And THIS connection is alive: no byte of the frame reached the wire, so the stream is
      // intact and the runtime is not in the lost state.
      Assert.False(runtime.HostLost,
          "a cancellation that reached no byte of the wire must not cost the connection");
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_CallerCancelDuringSend_ReleasesTheGate_AndChildSendsStillWork()
  {
    // Finding 2: a caller cancel used to return TurnCancelled BEFORE any teardown, leaving
    // the abandoned write holding the gate — so a CHILD send on the same transport blocked
    // unbounded. With the ruling the write is cancelled, which releases the gate.
    //
    // This peer never reads, so the cancellation reaches no byte of the frame and the stream
    // stays intact: the caller's stop is a turn-level failure and the connection lives on.
    NonReadingPeer peer = await NonReadingPeer.StartAsync().ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);
      using CancellationTokenSource turn = new();

      Task<Result<string>> pending = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-cancel", new string('x', 1024 * 1024), []), _ => { }, turn.Token);
      await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
      await turn.CancelAsync().ConfigureAwait(true);

      Result<string> result = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.False(result.IsSuccess);
      Assert.Equal("TurnCancelled", result.Error.Code);

      // THE POINT: the gate must be free, so a write on the same transport completes. The
      // peer drains first so the assertion measures the GATE, not a full pipe buffer.
      peer.StartDraining();
      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Task smallSend = app.SendAsync(new TransportEnvelope("ack", "\"1\"", 1),
          TestContext.Current.CancellationToken);
      Task smallDone = await Task.WhenAny(smallSend, Task.Delay(TimeSpan.FromSeconds(5),
          TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.True(smallDone == smallSend,
          "a caller cancel wedged the write gate: the cancelled write was abandoned, not cancelled");
      await smallSend.ConfigureAwait(true);
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_CancelledMidFrame_TearsDownTheConnection_AndSettlesTheChild()
  {
    // CONTROLLER RULING, the desync half. The peer consumes whole frames and then stalls
    // after the NEXT frame's 4-byte header, so the app's 1MB write cannot drain no matter
    // how long it is given: its header is on the wire and its payload never follows. When
    // the send bound cancels that write, the peer's frame stream is permanently
    // desynchronized — it will never parse another frame, and every later frame this side
    // writes is swallowed as this frame's payload.
    //
    // What must happen: the write is cancelled (releasing the gate) AND the connection is
    // torn down, so (a) HostLost reports the truth instead of a silently dead connection,
    // (b) a later turn refuses instead of hanging, and (c) a child started BEFORE the
    // failure completes with a declared failure instead of WhenSettledAsync never returning.
    NonReadingPeer peer = await NonReadingPeer.StartStallingAfterHeaderAsync(completeFrames: 1)
        .ConfigureAwait(true);
    NamedPipeChildTransport app = await ConnectAsync(peer.PipeName).ConfigureAwait(true);
    try
    {
      RemoteAgentRuntime runtime = new(app, new RootTurnBudget(
          TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10),
          TimeSpan.FromMilliseconds(400)));
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // A child run started BEFORE the failure: its settle must never hang.
      AgentId child = new(Guid.NewGuid());
      Result<AgentId> started = await runtime.Start(
          AgentRecord.Spawned(child, null, 1, "m/sub", "hung", "task", DateTimeOffset.UtcNow),
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(started.IsSuccess, started.Error?.Message);

      // The turn is sent FIRST and awaited second: the peer only consumes the header of the
      // frame it is stalling on, and that frame is this one. Waiting for the header before
      // sending would wait for a frame that does not exist yet.
      Task<Result<string>> pending = runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-desync", new string('x', 1024 * 1024), []), _ => { },
          TestContext.Current.CancellationToken);
      await WaitUntilAsync(() => peer.HeaderConsumed).ConfigureAwait(true);

      Result<string> turn = await pending.WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

      Assert.False(turn.IsSuccess);
      Assert.Equal("HostUnavailable", turn.Error.Code);
      // The diagnosis must admit the connection died, not advertise it as usable.
      Assert.Contains("desynchronized", turn.Error.Message, StringComparison.OrdinalIgnoreCase);

      // (a) The connection is DECLARED lost. Against the defect this is false: the write was
      // cancelled, the gate was released, and the runtime still believed it was connected
      // while the peer could never parse another frame.
      Assert.True(runtime.HostLost,
          "the connection was reported alive while its peer's frame stream was desynchronized");

      // (c) The child that was already running settles with a DECLARED failure. Against the
      // defect its WhenSettledAsync never returns — no settle, no failure, a silent hang.
      Result<AgentRunOutcome> settled = await runtime.WhenSettledAsync(child,
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.True(settled.IsSuccess, settled.Error?.Message);
      Assert.Equal(AgentStatus.Failed, settled.Value.Status);
      Assert.Contains("desynchronized", settled.Value.Report, StringComparison.OrdinalIgnoreCase);

      // (b) A later turn refuses at once instead of queueing on a connection nobody can use.
      Result<string> later = await runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-after", "hello", []), _ => { },
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      Assert.False(later.IsSuccess);
      Assert.Equal("HostUnavailable", later.Error.Code);
    }
    finally
    {
      await peer.DisposeAsync().ConfigureAwait(true);
      await app.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RootTurn_ReAttach_IsNotClobberedByTheStalePump()
  {
    // Finding 1: disposing the old transport faults the OLD pump, whose catch sets
    // _hostLost = true — AFTER ReplaceTransport cleared it. The fresh connection is then
    // refused as "not connected". Run the swap repeatedly: the race is intermittent, so one
    // pass proves little.
    for (int attempt = 0; attempt < 8; attempt++)
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using FakeConversationHost host = FakeConversationHost.Start();
      await using NamedPipeChildTransport first = await ConnectAsync(host.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
      RemoteAgentRuntime runtime = new(first);
      using CancellationTokenSource firstLoop = new();
      _ = runtime.RunReceiveLoopAsync(firstLoop.Token);

      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using FakeConversationHost second = FakeConversationHost.Start();
      await using NamedPipeChildTransport fresh = await ConnectAsync(second.PipeName).ConfigureAwait(true);
#pragma warning restore CA2007
      runtime.ReplaceTransport(fresh);
      using CancellationTokenSource secondLoop = new();
      _ = runtime.RunReceiveLoopAsync(secondLoop.Token);

      // Give the stale pump time to fault and (wrongly) clobber the flag.
      await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(true);

      Result<string> result = await runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-" + attempt, "hello", []), _ => { },
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(result.IsSuccess,
          "attempt " + attempt + ": the stale pump clobbered the fresh connection: " + result.Error?.Message);
    }
  }

  /// <summary>Connects the app side to the fake host's pipe, deadline-bounded.</summary>
  private static async Task<NamedPipeChildTransport> ConnectAsync(string pipeName)
      => await NamedPipeChildTransport.ConnectToHostAsync(pipeName, TestContext.Current.CancellationToken)
          .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

  /// <summary>Stands up the REAL ChildHostServer over a real pipe and returns the app
  ///     side of the connection. The serve loop runs until the pipe closes (its own
  ///     contract), so it is deliberately never awaited.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToRealHostAsync()
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-rootturn-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings,
        Path.Combine(Path.GetTempPath(), "ethang-rootturn-" + Guid.NewGuid().ToString("N") + ".db"));
    string pipeName = "ethang-rootturn-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
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

      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
