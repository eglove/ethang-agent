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
  private static readonly TimeSpan RootTurnBudget = TimeSpan.FromSeconds(1);

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
    Assert.Equal("released", settled.Value);
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
    RemoteAgentRuntime runtime = new(app, RootTurnBudget);
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
