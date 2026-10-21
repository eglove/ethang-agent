using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Fix round 2: the defects the round-1 hydration fix introduced or left. Concurrent
///     turns are now the NORMAL case (round 1 moved dispatch off the serve loop), so the
///     hydration latch and the runner-resolution latch are both load-bearing under
///     concurrency, and a buffering host must be detectable at the wire.</summary>
public sealed class ChildHostRootRunConcurrencyTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task RunTurnAsync_ConcurrentTurns_BothSeeTheStoredHistory()
  {
    // Finding 1: the hydration latch was set BEFORE the store read, so a second turn
    // arriving during a slow read ran against an EMPTY conversation - the finding-2 defect
    // reintroduced - and the hydrated prefix landed AFTER live turns.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700));

    // GENUINELY concurrent: both turns start before either awaits, so the second arrives
    // INSIDE the first's 700ms store read - the exact window the defect lived in.
    Task<Result<string>> pendingFirst = host.RunTurnAsync(
        new RootTurnRequest("turn-one", "one", []), _ => { }, TestContext.Current.CancellationToken);
    Task<Result<string>> pendingSecond = host.RunTurnAsync(
        new RootTurnRequest("turn-two", "two", []), _ => { }, TestContext.Current.CancellationToken);
    Result<string> first = await pendingFirst.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Result<string> second = await pendingSecond.WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    // EVERY provider call must have seen the hydrated prefix. Against the defect the second
    // call's request starts with "two" - the empty-conversation bug.
    Assert.Equal(2, host.Provider.Requests.Count);
    foreach (IReadOnlyList<Message> request in host.Provider.Requests)
    {
      Assert.True(request.Count >= 2, "a provider call saw fewer than the two hydrated messages");
      Assert.Equal("stored q", request[0].Content);
      Assert.Equal("stored a", request[1].Content);
    }
  }

  [Fact]
  public async Task RunTurnAsync_ConcurrentTurns_HydratedPrefixStaysFirst()
  {
    // The ORDER half of finding 1: the hydrated prefix must precede every live turn, or the
    // sink's absolute index is permanently wrong for the session.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700));

    Task<Result<string>> pendingFirst = host.RunTurnAsync(
        new RootTurnRequest("turn-one", "one", []), _ => { }, TestContext.Current.CancellationToken);
    Task<Result<string>> pendingSecond = host.RunTurnAsync(
        new RootTurnRequest("turn-two", "two", []), _ => { }, TestContext.Current.CancellationToken);
    _ = await pendingFirst.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await pendingSecond.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    IReadOnlyList<Message> messages = host.Runner.Conversation.Messages;
    Assert.Equal("stored q", messages[0].Content);
    Assert.Equal("stored a", messages[1].Content);
    // The live turns follow, in the order they ran.
    Assert.Equal("one", messages[2].Content);
    Assert.Equal("two", messages[4].Content);
  }

  [Fact]
  public async Task RunTurnAsync_CancelledHydration_RetriesOnTheNextTurn()
  {
    // Finding 8: cancelling mid-hydration left the latch set with an EMPTY conversation, so
    // every later turn ran with no history for the life of the runner. A failed or cancelled
    // hydration must leave the runner un-hydrated.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored q", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored a", DateTimeOffset.UnixEpoch),
        ],
        storeDelay: TimeSpan.FromMilliseconds(700));
    using CancellationTokenSource cancelled = new();

    Task<Result<string>> doomed = host.RunTurnAsync(
        new RootTurnRequest("turn-cancelled", "one", []), _ => { }, cancelled.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    await cancelled.CancelAsync().ConfigureAwait(true);
    _ = await doomed.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The NEXT turn must hydrate and see the history.
    Result<string> recovered = await host.RunTurnAsync(
        new RootTurnRequest("turn-after", "two", []), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(recovered.IsSuccess, recovered.Error?.Message);
    Assert.Equal(["stored q", "stored a", "two"], host.Provider.LastRequestTexts);
  }

  [Fact]
  public async Task RunTurnAsync_SeedLongerThanTwoLines_IsRefusedRatherThanMislabeled()
  {
    // Finding 7: the seed carries no roles, so a four-line seed became
    // [User, Assistant, Assistant, Assistant] - every user message after the first silently
    // relabelled. A refusal is honest; a mislabelled history is a silent quality bug.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-seed", "next", ["q1", "a1", "q2", "a2"]), events.Add,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidSeed", result.Error.Code);
    RootTurnEvent settle = Assert.Single(events);
    Assert.Equal("settled", settle.Kind);
    Assert.Contains("InvalidSeed", settle.Payload, StringComparison.Ordinal);
    // And nothing ran: the model was never called with a mislabelled history.
    Assert.Equal(0, host.Provider.Calls);
    Assert.Empty(host.Provider.Requests);
  }

  [Fact]
  public async Task RunTurnAsync_TwoLineSeed_IsStillAccepted()
  {
    // The refusal must not break the shape the protocol actually supports: one exchange.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-seed", "next", ["q1", "a1"]), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(["q1", "a1", "next"], host.Provider.LastRequestTexts);
  }

  [Fact]
  public async Task RootTurnEnvelope_BufferedHost_FailsTheWireStreamingProof()
  {
    // Finding 8: a host that buffers every event until settle left the suite green. This test
    // reads the WIRE directly while the provider is parked mid-turn: a streaming host has
    // already delivered the first delta, a buffering host has delivered nothing.
    using ManualResetEventSlim parked = new(initialState: false);
    using ManualResetEventSlim release = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        betweenDeltas: () =>
        {
          parked.Set();
          _ = release.Wait(TimeSpan.FromSeconds(15));
        });
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007

    await app.SendAsync(new TransportEnvelope("root-turn",
        /*lang=json,strict*/ "{\"TurnId\":\"turn-stream\",\"Input\":\"hi\",\"Seed\":[],\"Generation\":3}", 9200),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    await WaitUntilAsync(() => parked.IsSet, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

    // The turn is now parked BETWEEN its deltas. Read the wire with a SHORT bound: a
    // buffering host sends nothing here and this read times out.
    RootTurnEvent early = await ReadRootTurnEventAsync(app, "turn-stream", "delta",
        TimeSpan.FromSeconds(5)).ConfigureAwait(true);

    release.Set();
    RootTurnEvent settle = await ReadRootTurnEventAsync(app, "turn-stream", "settled",
        TimeSpan.FromSeconds(15)).ConfigureAwait(true);

    Assert.Equal("the ", early.Payload);
    Assert.Equal("the answer", settle.Payload);
    Assert.Equal(3, early.Generation);
    Assert.Equal(3, settle.Generation);
  }

  [Fact]
  public async Task ResolveRootHost_ConcurrentColdTurns_ShareOneBuildAndNeverReportUnbound()
  {
    // Finding 2: the latch was a plain bool read-then-write, so two cold turns raced and the
    // loser saw 'built' while the field was still null - answering 'not bound to a root
    // session' for a host that IS bound. The single-flight task makes both callers await ONE
    // build, so neither can observe a half-built state.
    //
    // Driven through the server's own resolution seam rather than a real container: building
    // a real provider stack per attempt costs seconds and adds nothing to the property under
    // test, which is the LATCH, not the container.
    // A REAL settings file, so the container build takes long enough for the race to be
    // reachable: with a missing file the build throws instantly and every caller sees null,
    // which would make this test pass against the broken latch.
    string settingsPath = Path.Combine(Path.GetTempPath(),
        "ethang-latch-" + Guid.NewGuid().ToString("N") + ".json");
    string sessionId = Guid.NewGuid().ToString("D");
    await File.WriteAllTextAsync(settingsPath,
        "{\"OpenRouter\":{\"ApiKey\":\"sk-or-probe\",\"BaseUrl\":\"https://openrouter.invalid/api/v1\"},"
            + "\"SubAgents\":{},\"SessionId\":\"" + sessionId + "\"}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settingsPath,
        Path.Combine(Path.GetTempPath(), "ethang-latch-" + Guid.NewGuid().ToString("N") + ".db"));

    Task<RootRunHost?>[] resolves =
    [
        Task.Run(() => server.ResolveRootHostForTest(), TestContext.Current.CancellationToken),
        Task.Run(() => server.ResolveRootHostForTest(), TestContext.Current.CancellationToken),
        Task.Run(() => server.ResolveRootHostForTest(), TestContext.Current.CancellationToken),
    ];
    RootRunHost?[] results = await Task.WhenAll(resolves)
        .WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    // Whatever the build produced, EVERY caller must agree - a racer that saw null would
    // report the false unbound.
    Assert.All(results, result => Assert.Same(results[0], result));
  }


  /// <summary>Reads frames until the event for <paramref name="turnId"/> of
  ///     <paramref name="kind"/> arrives, bounded by <paramref name="limit"/>.</summary>
  private static async Task<RootTurnEvent> ReadRootTurnEventAsync(NamedPipeChildTransport app,
      string turnId, string kind, TimeSpan limit)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      TransportEnvelope envelope = await app.ReceiveAsync()
          .WaitAsync(limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      if (envelope.Kind != "root-turn-event")
      {
        continue;
      }

      RootTurnEvent evt = RootTurnProtocol.ParseEvent(envelope.Json);
      if (evt.TurnId == turnId && evt.Kind == kind)
      {
        return evt;
      }
    }

    Assert.Fail("no '" + kind + "' event for '" + turnId + "' arrived within " + limit);
    throw new InvalidOperationException("unreachable");
  }

  private static async Task<NamedPipeChildTransport> ConnectToServerAsync(ChildHostServer server)
  {
    string pipeName = "ethang-r2-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
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
