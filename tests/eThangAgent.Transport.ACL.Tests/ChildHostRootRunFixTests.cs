using eThangAgent.AgentDomain;
using eThangAgent.ChildHost;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>Fix round 1's findings, each pinned by a test that fails against the first
///     implementation: a malformed turn must not kill the host, a turn must not block the
///     serve loop, deltas must reach the wire before the turn settles, and the host's real
///     settings-to-container resolution must actually produce a runner.</summary>
public sealed class ChildHostRootRunFixTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

  [Fact]
  public async Task RootTurnEnvelope_MalformedInput_DoesNotKillTheHost()
  {
    // Finding 1: ParseRequest threw on a non-string Input, the exception escaped ServeAsync,
    // and the host process exited - taking every running child with it. A peer fault must
    // cost the TURN, never the host.
    //
    // Driven at the WIRE level rather than through RemoteAgentRuntime: the client mints its
    // own per-turn generation, so it would drop a raw envelope's settle as stale and the
    // parse guard would be unobservable. Reading frames directly pins the guard itself.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync(host.Runner).ConfigureAwait(true);
#pragma warning restore CA2007

    // An ADDRESSABLE turn whose Input is not a string: the turn id survives, the payload
    // does not.
    await app.SendAsync(new TransportEnvelope("root-turn",
        /*lang=json,strict*/ "{\"TurnId\":\"turn-bad\",\"Input\":123,\"Seed\":[],\"Generation\":7}", 9001),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    RootTurnEvent settle = await ReadRootTurnEventAsync(app, "turn-bad").ConfigureAwait(true);
    Assert.Equal("settled", settle.Kind);
    Assert.Equal("turn-bad", settle.TurnId);
    Assert.Contains("InvalidRootTurn", settle.Payload, StringComparison.Ordinal);
    // The generation is echoed even on this path: the app can only match the settle to its
    // live turn when the value comes back verbatim.
    Assert.Equal(7, settle.Generation);

    // And the host is ALIVE: a well-formed turn still settles.
    await app.SendAsync(new TransportEnvelope("root-turn",
        /*lang=json,strict*/ "{\"TurnId\":\"turn-after\",\"Input\":\"hello\",\"Seed\":[],\"Generation\":8}", 9002),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    RootTurnEvent answered = await ReadRootTurnEventAsync(app, "turn-after", "settled")
        .ConfigureAwait(true);
    Assert.Equal("the answer", answered.Payload);
  }

  /// <summary>Reads frames until the FIRST root-turn event for <paramref name="turnId"/>,
  ///     ignoring acks, declares, and events belonging to other turns. Selecting by turn id
  ///     is what makes the assertion about THIS turn rather than about frame ordering.</summary>
  private static async Task<RootTurnEvent> ReadRootTurnEventAsync(NamedPipeChildTransport app,
      string turnId, string kind = "settled")
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + Limit;
    while (DateTimeOffset.UtcNow < deadline)
    {
      TransportEnvelope envelope = await app.ReceiveAsync().WaitAsync(Limit,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
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

    Assert.Fail("no root-turn event for '" + turnId + "' arrived within the limit");
    throw new InvalidOperationException("unreachable");
  }

  [Fact]
  public async Task RootTurnEnvelope_InFlightTurn_DoesNotBlockOtherEnvelopes()
  {
    // Finding 4: the serve loop awaited each turn inline, so a slow turn stopped the host
    // processing ANY envelope - start, deliver, interrupt. A gate parks turn one inside the
    // provider while a DELIVER envelope is sent: the host must still reach its mailbox.
    using ManualResetEventSlim gate = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        betweenDeltas: () => gate.Wait(TimeSpan.FromSeconds(15)));
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToServerAsync(host.Server).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);

    // A steering target the host can resolve, registered through the server's own seam.
    AgentId child = AgentId.NewId();
    ChildMailboxRegistry mailboxes = new();
    RecordingMailbox box = new();
    mailboxes.Register(child, box);
    host.Server.AttachDeliveryRegistry(child.Value, mailboxes);

    Task<Result<string>> turn = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-slow", "hello", []), _ => { },
        TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => host.ProviderCalls > 0).ConfigureAwait(true);

    // Turn one is parked inside the provider. A deliver envelope must still be handled.
    await app.SendAsync(new TransportEnvelope("deliver",
        /*lang=json,strict*/ "{\"RecordId\":\"" + child.Value + "\",\"Text\":\"steer\",\"Urgency\":1,\"Sender\":\"app\"}",
        9100), TestContext.Current.CancellationToken).ConfigureAwait(true);
    await WaitUntilAsync(() => box.UnreadCount > 0, TimeSpan.FromSeconds(8)).ConfigureAwait(true);
    bool reached = box.UnreadCount > 0;
    gate.Set();
    _ = await turn.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(reached, "a deliver envelope was blocked behind an in-flight turn: the serve loop awaits turns inline");
  }

  /// <summary>A mailbox that only records delivery, so a test can observe the host's
  ///     deliver path without a running child.</summary>
  private sealed class RecordingMailbox : IAgentMailbox
  {
    private int _unread;

    public int UnreadCount => Volatile.Read(ref _unread);

    public Result<bool> Deliver(PendingMessage message)
    {
      _ = Interlocked.Increment(ref _unread);
      return Result.Success(true);
    }

    public IReadOnlyList<PendingMessage> Drain() => [];
  }

  [Fact]
  public async Task RootTurnEnvelope_DeltasReachTheWireBeforeTheTurnSettles()
  {
    // Finding 8: buffering every event until settle left the suite green, so the property
    // the app actually experiences had no test. The provider parks between its two deltas
    // until the FIRST delta has crossed the wire - a buffering host deadlocks and times out.
    using ManualResetEventSlim firstDeltaSeen = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        betweenDeltas: () => firstDeltaSeen.Wait(TimeSpan.FromSeconds(10)));
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync(host.Runner).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Task<Result<string>> turn = runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-stream", "hello", []),
        evt =>
        {
          events.Add(evt);
          if (evt.Kind == "delta")
          {
            firstDeltaSeen.Set();
          }
        },
        TestContext.Current.CancellationToken);

    Task finished = await Task.WhenAny(turn, Task.Delay(Limit, TestContext.Current.CancellationToken))
        .ConfigureAwait(true);
    Assert.True(finished == turn,
        "the turn never completed: the first delta was not on the wire while the turn was still running");
    Result<string> result = await turn.ConfigureAwait(true);
    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("the answer", result.Value);
  }

  [Fact]
  public async Task RootTurnEnvelope_BufferedEvents_FailTheStreamingProof()
  {
    // The negative control for the test above, so the streaming assertion cannot pass by
    // accident: with the gate never released the turn must NOT complete, proving the test
    // really observes the mid-turn wire state rather than a turn that finished anyway.
    using ManualResetEventSlim neverReleased = new(initialState: false);
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        betweenDeltas: () => neverReleased.Wait(TimeSpan.FromMilliseconds(600)));
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using NamedPipeChildTransport app = await ConnectToRealHostAsync(host.Runner).ConfigureAwait(true);
#pragma warning restore CA2007
    RemoteAgentRuntime runtime = new(app);
    using CancellationTokenSource loop = new();
    _ = runtime.RunReceiveLoopAsync(loop.Token);
    List<RootTurnEvent> events = [];

    Result<string> result = await runtime.SendRootTurnAsync(
        new RootTurnRequest("turn-gated", "hello", []), events.Add,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    // The provider waited its 600ms and then continued: the turn still completes, and the
    // FIRST delta was already delivered before the wait elapsed.
    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal("delta", events[0].Kind);
  }

  [Fact]
  public async Task RunTurnAsync_Seed_CarriesThePriorHistoryToTheModel()
  {
    // Finding 2: the host conversation was NEVER hydrated, so every host turn sent the
    // model exactly one user message and no history - while the transcript sink appended
    // the turn after rows the model never saw. The seed exists for exactly this.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-seeded", "and then?", ["first question", "first answer"]),
        events.Add, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    // The model must have SEEN the prior exchange plus this turn's input.
    Assert.Equal(["first question", "first answer", "and then?"],
        host.Provider.LastRequestTexts);
    Assert.Equal(["User", "Assistant", "User"], host.Provider.LastRequestRoles);
  }

  [Fact]
  public async Task RunTurnAsync_StoreTranscript_IsPreferredOverTheSeed()
  {
    // The store is authoritative: a resumed session's rows are what the app already showed
    // the user, so a seed from the app never overrides them.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory:
        [
            new Message(Role.User, "stored question", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored answer", DateTimeOffset.UnixEpoch),
        ]);
    List<RootTurnEvent> events = [];

    Result<string> result = await host.RunTurnAsync(
        new RootTurnRequest("turn-stored", "next", ["seed question", "seed answer"]),
        events.Add, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess, result.Error?.Message);
    Assert.Equal(["stored question", "stored answer", "next"], host.Provider.LastRequestTexts);
  }

  [Fact]
  public async Task RunTurnAsync_Hydration_DoesNotNotifySubscribers()
  {
    // Hydration must not look like fresh model output. The transcript sink subscribes the
    // conversation's mutation notification, so a hydrated message that RAISED it would be
    // appended to the store a second time. Observed at the conversation itself, which is
    // the exact seam the sink listens on.
    // Three runs of the SAME turn: no history, a two-line seed, and a STORE transcript.
    // Whatever the agent notifies for its own messages is the baseline; neither hydration
    // path may add to it.
    int unseeded = await CountNotificationsAsync(seed: [], stored: []).ConfigureAwait(true);
    int seeded = await CountNotificationsAsync(seed: ["prior one", "prior two"], stored: [])
        .ConfigureAwait(true);
    int stored = await CountNotificationsAsync(seed: [], stored:
        [
            new Message(Role.User, "stored one", DateTimeOffset.UnixEpoch),
            new Message(Role.Assistant, "stored two", DateTimeOffset.UnixEpoch),
        ]).ConfigureAwait(true);

    // Hydration contributes ZERO notifications on BOTH paths. Routing either through Add*
    // would make that run notify twice more, and the store would hold the history twice.
    Assert.Equal(unseeded, seeded);
    Assert.Equal(unseeded, stored);
    Assert.True(unseeded > 0, "the turn itself must notify, or this comparison proves nothing");
  }
  [Fact]
  public void SessionHost_WithSessionIdInSettings_ExposesARootRunner()
  {
    // Finding 6: mutating BuildRootRun to return null left the suite green, because the
    // tests reach the runner only through AttachRootRun. This drives the REAL path:
    // settings JSON -> SessionId -> composed container -> RootRun.
    string settingsPath = Path.Combine(Path.GetTempPath(),
        "ethang-rootrun-settings-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(settingsPath, SettingsJson(Guid.NewGuid().ToString("D")));
    try
    {
      SessionHost host = SessionHost.Create(settingsPath,
          Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".db"));

      Assert.NotNull(host.RootRun);
    }
    finally
    {
      File.Delete(settingsPath);
    }
  }

  [Fact]
  public void SessionHost_WithoutSessionIdInSettings_HasNoRootRunner()
  {
    // The other half of the same resolution: a host bound to no session must not guess one.
    string settingsPath = Path.Combine(Path.GetTempPath(),
        "ethang-rootrun-settings-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(settingsPath, SettingsJson(sessionId: null));
    try
    {
      SessionHost host = SessionHost.Create(settingsPath,
          Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".db"));

      Assert.Null(host.RootRun);
    }
    finally
    {
      File.Delete(settingsPath);
    }
  }

  [Fact]
  public void RootRunHost_FillsTheContainersRootSessionIdentity()
  {
    // Finding 7: the identity fill was entirely unverified - removing it left 7/7 green.
    // Without it the container's RootTranscriptSink resolves no root id and is INERT, so a
    // host turn runs and persists nothing.
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"]);

    RootSessionIdentity identity = host.RootIdentity;

    Assert.True(identity.Id is not null, "the runner must fill the container's root identity or the transcript sink stays inert");
    Assert.Equal(host.Runner.RootId, identity.Id);
  }

  [Fact]
  public void RootRunHost_DoesNotOverwriteAnAlreadyFilledIdentity()
  {
    // The app factory fills the identity before the container is handed out; the runner's
    // fill is a fallback for the host's empty carrier, never a clobber.
    RootSessionIdentity preFilled = new() { Id = new AgentId(Guid.NewGuid()) };
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        options: new ScriptedOptions(Identity: preFilled));

    Assert.Equal(preFilled.Id, host.RootIdentity.Id);
    Assert.NotEqual(host.Runner.RootId, host.RootIdentity.Id);
  }

  /// <summary>The host settings JSON the supervisor writes, with the root session bound.</summary>
  private static string SettingsJson(string? sessionId)
  {
    string session = sessionId is null ? "null" : "\"" + sessionId + "\"";
    return "{\"OpenRouter\":{\"ApiKey\":\"sk-or-probe\",\"BaseUrl\":\"https://openrouter.invalid/api/v1\"},"
        + "\"SubAgents\":{},\"SessionId\":" + session + "}";
  }

  /// <summary>Runs one turn and reports how many conversation notifications it raised.</summary>
  private static async Task<int> CountNotificationsAsync(IReadOnlyList<string> seed,
      IReadOnlyList<Message> stored)
  {
    using ScriptedRootHost host = ScriptedRootHost.Start("the answer", ["the ", "answer"],
        storedHistory: stored);
    int count = 0;
    using IDisposable lease = host.Runner.Conversation.Subscribe(_ =>
        Interlocked.Increment(ref count));
    _ = await host.RunTurnAsync(new RootTurnRequest("turn-1", "hello", seed), _ => { },
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    return Volatile.Read(ref count);
  }

  /// <summary>Connects the app side to an ALREADY-BUILT server, so a test that attaches
  ///     registries to <c>ScriptedRootHost.Server</c> drives the SAME server instance.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToServerAsync(ChildHostServer server)
  {
    string pipeName = "ethang-rootrun-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }

  /// <summary>Stands up the REAL server over a real pipe with the runner attached.</summary>
  private static async Task<NamedPipeChildTransport> ConnectToRealHostAsync(RootRunHost runner)
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings,
        /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"SubAgents\":{}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    ChildHostServer server = new(settings,
        Path.Combine(Path.GetTempPath(), "ethang-rootrun-" + Guid.NewGuid().ToString("N") + ".db"));
    server.AttachRootRun(runner);
    string pipeName = "ethang-rootrun-" + Guid.NewGuid().ToString("N");
    Task<NamedPipeChildTransport> accept = NamedPipeChildTransport.AcceptAppAsync(pipeName,
        TestContext.Current.CancellationToken);
    NamedPipeChildTransport app = await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
        TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    NamedPipeChildTransport hostSide = await accept.WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
    server.AttachTransport(hostSide);
    _ = server.ServeAsync();
    return app;
  }

  private static async Task WaitUntilAsync(Func<bool> condition)
      => await WaitUntilAsync(condition, Limit).ConfigureAwait(true);

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
