using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The host's ROOT turn runner (spec D6, task 9): the conversation's own agent runs
///     HERE, in the host process, and streams its turn back as events.
///
///     Anti-tautology: <see cref="ScriptedRootHost"/> builds the REAL
///     <see cref="RootRunHost"/> over a REAL composed container whose only fake is the model
///     provider, so these tests fail if the host's turn path, its callback mapping, or its
///     event emission is wrong - not merely if a stub was called.</summary>
public sealed class ChildHostRootRunTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

  /// <summary>Stands up the REAL <see cref="ChildHostServer"/> over a real pipe and returns
  ///     the app side. <paramref name="runner"/> is attached through the server's own seam
  ///     (the way a delivery registry is), so the dispatch path under test is the production
  ///     one. The serve loop runs until the pipe closes (its own contract) and is never
  ///     awaited.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToRealHostAsync(RootRunHost? runner)
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings, /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"SubAgents\":{}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings,
        Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".db"));
    if (runner is not null)
    {
      server.AttachRootRun(runner);
    }

    string pipeName = "ethang-rootrun-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName, TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }

  [Fact]
  public async Task RunTurnAsync_UserInput_StreamsDeltasAndSettlesWithTheAnswer()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("the answer", result.Value);
    Assert.Equal(["delta", "delta", "settled"], events.Select(e => e.Kind));
    Assert.Equal(["the ", "answer", "the answer"], events.Select(e => e.Payload));
    Assert.All(events, e => Assert.Equal("turn-1", e.TurnId));
  }

  [Fact]
  public async Task RunTurnAsync_EmptyInput_FailsWithTypedError()
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("unused", ["unused"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-1", "   ", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidMessage", result.Error.Code);
    // A rejected turn never STARTS, but it still SETTLES: the app reads the outcome from the
    // settled event, so a silent early return would report a transport fault instead of this
    // typed refusal (fix round 1, finding 3).
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("InvalidMessage", settle.Payload, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunTurnAsync_NonZeroGeneration_IsEchoedOnEveryEvent()
  {
    // The app mints a fresh generation per turn and drops any event whose generation does
    // not match the live turn (RootTurnEvent's documented protocol requirement). A host
    // that omits the echo has EVERY event silently discarded.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-gen", "hello", [], Generation: 42), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(3, events.Count);
    Assert.All(events, e => Assert.Equal(42, e.Generation));
    Assert.All(events, e => Assert.Equal("turn-gen", e.TurnId));
  }

  [Fact]
  public async Task RunTurnAsync_StreamsEachDeltaBeforeTheTurnReturns()
  {
    // Streaming, not collecting: the provider records how many events had reached the
    // caller BETWEEN its two deltas. A buffered host reports 0 there.
    List<RootTurnEvent> events = [];
    int observedMidTurn = -1;
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        betweenDeltas: () => observedMidTurn = events.Count);

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(1, observedMidTurn);
  }

  [Fact]
  public async Task RootTurnEnvelope_OverTheRealServer_RunsTheTurnAndStreamsItsEvents()
  {
    // The seam this task replaces: the server used to answer every root turn with a typed
    // refusal. Over a REAL pipe, a real ChildHostServer must now dispatch the envelope to
    // the runner and put every event it emits on the wire, in order.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync(host.Runner).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-server", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("the answer", result.Value);
    Assert.Equal(["delta", "delta", "settled"], events.Select(e => e.Kind));
    Assert.Equal(["the ", "answer", "the answer"], events.Select(e => e.Payload));
    Assert.All(events, e => Assert.Equal("turn-server", e.TurnId));
    // The generation the CLIENT minted must come back on every event, or the client drops
    // them all and the turn fails on its idle bound.
    Assert.All(events, e => Assert.NotEqual(0, e.Generation));
    _ = Assert.Single(events.Select(e => e.Generation).Distinct());
  }

  [Fact]
  public async Task RootTurnEnvelope_UnboundHost_SettlesWithATypedRefusal()
  {
    // A host whose settings name no root session cannot know which conversation a turn
    // belongs to: it must answer with a typed settle, never silence.
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync(runner: null).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-unbound", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("RootTurnUnavailable", settle.Payload, StringComparison.Ordinal);
  }
  [Fact]
  public async Task RunTurnAsync_ProviderFailure_SettlesWithTheTypedErrorPayload()
  {
    // A failed turn still SETTLES: the app renders the failure from the settled event, so
    // a host that only settles success leaves its caller waiting on the idle bound.
    using ScriptedRootHost host = ScriptedRootHost.Start("unused", [],
        providerError: new DomainError("ProviderError", "boom"));
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-1", "hello", []), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Contains("boom", result.Error.Message, StringComparison.Ordinal);
    // The loop surfaces its own failure notice too, so the settle is the LAST event, not
    // the only one - but it must be there, or the caller waits out the idle bound.
    RootTurnEvent settle = events[^1];
    Assert.Equal("settled", settle.Kind);
    _ = Assert.Single(events, e => e.Kind == "settled");
    Assert.Equal("Error [" + result.Error.Code + "]: " + result.Error.Message, settle.Payload);
  }
}
