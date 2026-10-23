using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Spec D6, the thin client: the app runs NO agent work. A turn travels to this
///     conversation's host process and its events stream back into the SAME transcript
///     surface the in-process path rendered, so nothing the user sees changes when the
///     agent moves out of process.
/// <para>Every test here wires a fake <see cref="IRootTurnChannel"/> — the host — and a
///     turn runner that RECORDS any invocation. The runner must never fire: a turn that
///     reached it would mean the app is still running agent work, which is the exact
///     regression these tests exist to catch.</para></summary>
public sealed class ThinClientTurnTests
{
  /// <summary>A minimal valid 1x1 PNG: AttachImageBytes sniffs magic bytes, and the
  ///     transcript decodes the bitmap, so a fake header would fail both.</summary>
  private static byte[] OnePixelPng() => Convert.FromBase64String(
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

  [Fact]
  public async Task SendAsync_HostStreamsDeltas_TranscriptShowsThemInOrder()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["he", "llo"]).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Contains("hello", session.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
    Assert.Equal(0, session.Runner.Invocations);
  }

  [Fact]
  public async Task RequestStop_DuringHostTurn_CancelsTheTurnAndShowsANotice()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["he"], holdOpen: true).ConfigureAwait(true);

    Task send = session.ViewModel.SubmitAsync("hi");
    await session.Host.TurnStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    session.ViewModel.RequestStop();
    await send.ConfigureAwait(true);

    Assert.Contains(session.ViewModel.Transcript.Notices,
        n => n.Contains("stopped", StringComparison.OrdinalIgnoreCase));
    Assert.Equal(0, session.Runner.Invocations);
  }

  [Fact]
  public async Task SendAsync_CarriesTheLiveModelChoice_OnTheTurnRequest()
  {
    // 2026-10-22: the picker lives in the app and the agent lives in the host. A choice
    // that did not travel never reached the serving model, so every host turn ran the
    // fallback while the status bar showed the user's pick.
    SessionModelPreferences preferences = new() { ModelId = "minimax/minimax-m3.1-flash:free" };
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"], preferences: preferences).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    RootTurnRequest sent = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
    string carrier = Assert.IsType<string>(sent.ModelPreferences);
    Result<SessionModelPreferences> decoded = SessionModelPreferencesJson.Deserialize(carrier);
    Assert.True(decoded.IsSuccess, decoded.Error?.Message);
    Assert.Equal("minimax/minimax-m3.1-flash:free", decoded.Value.ModelId);
  }

  [Fact]
  public async Task SendAsync_CarriesTheEffortKnob_Too()
  {
    SessionModelPreferences preferences = new() { ModelId = "m/x", ReasoningEffort = ReasoningEffort.Max };
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"], preferences: preferences).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Result<SessionModelPreferences> decoded =
        SessionModelPreferencesJson.Deserialize(session.Host.LastRequest!.ModelPreferences!);
    Assert.True(decoded.IsSuccess, decoded.Error?.Message);
    Assert.Equal(ReasoningEffort.Max, decoded.Value.ReasoningEffort);
  }

  [Fact]
  public async Task SendAsync_WithHostChannel_NeverInvokesTheInProcessRunner()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    // The thin-client invariant: the app's own handler was never touched.
    Assert.Equal(0, session.Runner.Invocations);
    Assert.Equal(1, session.Host.TurnsSent);
    Assert.Equal("hi", session.Host.LastRequest!.Input, StringComparer.Ordinal);
  }

  [Fact]
  public async Task SendAsync_HostTransportFailure_SurfacesATypedErrorNotice()
  {
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    session.Host.FailWith = new DomainError("HostUnavailable", "the conversation host is not connected.");

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    // A transport failure must reach the user as a typed error - never a hang, never a
    // silently empty answer.
    Assert.Contains(session.ViewModel.Transcript.Notices,
        n => n.Contains("Error [HostUnavailable]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SendAsync_WithStagedImage_CarriesItOnTheRequest()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["seen"]).ConfigureAwait(true);
    byte[] png = OnePixelPng();
    Assert.True(session.ViewModel.AttachImageBytes(png, "shot.png"));

    await session.ViewModel.SubmitAsync("what is this").ConfigureAwait(true);

    // The image must TRAVEL: the host has no clipboard, so a request without it means the
    // model never sees the image the transcript is showing.
    RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
    RootTurnImage image = Assert.Single(request.Images);
    Assert.Equal("image/png", image.MediaType, StringComparer.Ordinal);
    Assert.Equal(Convert.ToBase64String(png), image.Base64Data, StringComparer.Ordinal);
  }

  [Fact]
  public async Task SubmitAsync_SlashInvocation_CarriesTheSystemLineAsPreTurn()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["done"], skillName: "deploy").ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("/deploy staging").ConfigureAwait(true);

    // The invocation line is a CONVERSATION mutation the app made; the host owns the
    // conversation the model reads, so it must travel with the turn.
    RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
    RootTurnPreTurnMessage preTurn = Assert.Single(request.PreTurn);
    Assert.Equal("system", preTurn.Role, StringComparer.Ordinal);
    Assert.Contains("[skill invoked: deploy]", preTurn.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunCommand_BangCommand_CarriesTheModelFacingLineAsPreTurn()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["noted"], commandLine: true).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("! echo hi").ConfigureAwait(true);
    await session.ViewModel.SubmitAsync("and then?").ConfigureAwait(true);

    // The command's model-facing line queues out of turn and rides the NEXT turn; without
    // this the model can never discover command_output.
    RootTurnRequest request = Assert.IsType<RootTurnRequest>(session.Host.LastRequest);
    RootTurnPreTurnMessage preTurn = Assert.Single(request.PreTurn);
    // The line is CommandRun.ModelFacingLine - the app's own rendering, carried verbatim.
    Assert.Contains("User ran: `echo hi`", preTurn.Text, StringComparison.Ordinal);
    Assert.Contains("id: 7", preTurn.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RequestStop_DuringHostTurn_InterruptsTheTurnInTheHost()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["he"], holdOpen: true).ConfigureAwait(true);

    Task send = session.ViewModel.SubmitAsync("hi");
    await session.Host.TurnStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    session.ViewModel.RequestStop();
    await send.ConfigureAwait(true);

    // Cancelling the local token alone abandons only THIS process's wait; the host would
    // keep running and persisting the turn. The interrupt must reach the host by turn id.
    string interrupted = Assert.Single(session.Host.InterruptedTurnIds);
    Assert.Equal(session.Host.LastRequest!.TurnId, interrupted, StringComparer.Ordinal);
  }

  [Fact]
  public async Task SendAsync_HostStreamsThenSettlesAnError_ShowsTheError()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["partial answ"]).ConfigureAwait(true);
    session.Host.SettleWith = "Error [ProviderError]: the provider refused mid-stream.";

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    // A settle carrying an error is a FAILED turn even though the transport succeeded and
    // text already streamed: the in-process path showed both, and a truncated answer with
    // no error is the silent lie this guards.
    Assert.Contains(session.ViewModel.Transcript.Notices,
        n => n.Contains("Error [ProviderError]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SendAsync_HostNoticeEvent_RendersAsANotice()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["ok"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "notice", "model selected: x", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Contains(session.ViewModel.Transcript.Notices,
        n => n.Contains("model selected: x", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SendAsync_HostReasoningEvents_RenderAsReasoningEntries()
  {
    // Issue #245: the model's streaming thoughts must reach the chat. The host forwards
    // them as the `reasoning` wire kind; the app renders the same ReasoningEntry the
    // in-process path produced. Dropped, the chat showed no thinking at all.
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "reasoning", "thinking out loud", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    ReasoningEntry entry = Assert.IsType<ReasoningEntry>(session.ViewModel.Transcript.Entries
        .First(e => e is ReasoningEntry));
    Assert.Contains("thinking out loud", entry.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_HostToolResultPayload_RendersTheFullContent()
  {
    // Issue #245: the legacy mapping passed the SUMMARY as the full content, so every
    // tool card body read "ok". The JSON payload's content must land on the card.
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "tool",
        RootTurnToolResultPayload.Serialize("exec", "ok", "parse names", "ok", "42"), 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    ToolResultEntry result = Assert.IsType<ToolResultEntry>(session.ViewModel.Transcript.Entries
        .First(e => e is ToolResultEntry));
    Assert.Equal("42", result.FullContent, StringComparer.Ordinal);
    Assert.Equal("parse names", result.HeaderTitle, StringComparer.Ordinal);
  }

  [Fact]
  public async Task SendAsync_ChildEventEnvelope_AddsASubagentRow()
  {
    // Issue #245: the host forwards child events as child-event envelopes; the runtime
    // republishes them onto the session's stream, and the panel's subscription adds a
    // row. The full app-side path: wire envelope -> runtime -> IAgentEvents -> panel.
    FakeHostSession session = await FakeHostSession.StartAsync(["ok"]).ConfigureAwait(true);
    TestEventStream events = new();
    session.ViewModel.Subagents.Attach(events, apply => evt => apply(evt));
    AgentId childId = AgentId.NewId();

    // The runtime republishes onto the stream the panel subscribed (the production
    // wiring resolves the SAME IAgentEvents for both). Simulate the pump's publish:
    // the runtime's HandleChildEvent is private, so the test drives the observable
    // contract - an event on the stream must reach the panel.
    events.Publish(new ChildStartedEvent(childId, DateTimeOffset.UtcNow, null, "mock/sub-model", 0));

    SubagentRowViewModel row = Assert.Single(session.ViewModel.Subagents.Rows);
    Assert.Equal(childId, row.Id);
    Assert.True(row.IsRunning);
  }

  [Fact]
  public async Task SendAsync_UnreadableToolPayload_SurfacesANotice()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["ok"]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "tool", "garbage payload", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    // A dropped tool event would hide a whole step of the turn: it must be visible.
    Assert.Contains(session.ViewModel.Transcript.Notices,
        n => n.Contains("unreadable tool event", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SendAsync_HostToolEvents_RenderCallAndResultCards()
  {
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    session.Host.Script.Add(new RootTurnEvent("t", "tool", "call exec 1/1 {\"program\":\"return 1;\"}", 0));
    session.Host.Script.Add(new RootTurnEvent("t", "tool", "result exec ok 1 line", 0));

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    ToolCallEntry call = Assert.IsType<ToolCallEntry>(session.ViewModel.Transcript.Entries
        .First(e => e is ToolCallEntry));
    Assert.Equal("exec", call.Name, StringComparer.Ordinal);
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(session.ViewModel.Transcript.Entries
        .First(e => e is ToolResultEntry));
    Assert.Equal("exec", result.Name, StringComparer.Ordinal);
    Assert.False(result.IsError);
    Assert.Equal("1 line", result.Summary, StringComparer.Ordinal);
  }
}

/// <summary>One view-model wired the thin-client way: a fake host channel that streams
///     scripted events, plus a turn runner that records any invocation so a test can prove
///     the app never ran the turn itself.</summary>
internal sealed class FakeHostSession
{
  private FakeHostSession(AgentSessionViewModel vm, FakeRootTurnChannel host, RecordingTurnRunner runner)
  {
    ViewModel = vm;
    Host = host;
    Runner = runner;
  }

  public AgentSessionViewModel ViewModel { get; }

  public FakeRootTurnChannel Host { get; }

  public RecordingTurnRunner Runner { get; }

  /// <summary>Builds the session over a host that streams <paramref name="deltas"/> in
  ///     order and then settles with their concatenation. <paramref name="holdOpen"/> keeps
  ///     the turn in flight until it is cancelled (the stop-button shape).</summary>
  public static Task<FakeHostSession> StartAsync(IReadOnlyList<string> deltas, bool holdOpen = false,
      string? skillName = null, bool commandLine = false, SessionModelPreferences? preferences = null)
  {
    ArgumentNullException.ThrowIfNull(deltas);
    FakeRootTurnChannel host = new(deltas, holdOpen);
    RecordingTurnRunner runner = new();
    AgentSessionViewModel vm = new(
        runner.RunAsync,
        new RootSessionLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          RootTurnChannel = host,
          SkillInvocation = skillName is null ? null : new SkillInvocationService(new FakeSkillCatalog(skillName)),
          SkillCatalogSource = skillName is null ? null : () => [],
          CommandRunner = commandLine ? new FakeCommandRunner() : null,
          ModelPreferences = preferences,
        });
    return Task.FromResult(new FakeHostSession(vm, host, runner));
  }
}

/// <summary>The fake host: answers each turn with the scripted event sequence, then
///     settles. It ECHOES the request's generation on every event, which is the protocol
///     requirement a real host honors - a fake that omitted it would have every event
///     dropped by the wire runtime and would prove nothing about the app's mapping.</summary>
internal sealed class FakeRootTurnChannel(IReadOnlyList<string> deltas, bool holdOpen) : IRootTurnChannel
{
  /// <summary>Keeps every turn in flight until it is cancelled — the stop-button shape, and
  ///     the deterministic way to hold a turn open while another path (a session-request's
  ///     tab swap) waits for it to go idle.</summary>
  public bool HoldOpen { get; set; } = holdOpen;

  private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Events emitted verbatim after the scripted deltas (the tool/notice kinds).</summary>
  public List<RootTurnEvent> Script { get; } = [];

  /// <summary>When set, the turn fails at the transport instead of settling.</summary>
  public DomainError? FailWith { get; set; }

  /// <summary>When set, the turn settles with THIS payload instead of the answer — the
  ///     host's typed failure line, which the app must surface even after streaming text.</summary>
  public string? SettleWith { get; set; }

  public int TurnsSent { get; private set; }

  public RootTurnRequest? LastRequest { get; private set; }

  /// <summary>Completes once a turn is in flight — the deterministic 'busy' signal a
  ///     stop test needs instead of a sleep.</summary>
  public Task TurnStarted => _started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

  /// <summary>Turn ids the app interrupted IN THE HOST. This is the distinction the stop
  ///     test turns on: a fake that merely honors its token cannot tell 'the host turn was
  ///     cancelled' from 'the caller abandoned its local wait'.</summary>
  public List<string> InterruptedTurnIds { get; } = [];

  public void InterruptRootTurn(string turnId) => InterruptedTurnIds.Add(turnId);

  /// <summary>Answers the app sent for session-requests (issue #234): the request id with
  ///     either the new session's id or the refusal's typed code. The host side of the wire
  ///     is covered in Transport.ACL tests; here it is the app's OUTBOUND answer that
  ///     matters — a request the app never answers leaves the tool waiting out its budget.</summary>
  public List<(string RequestId, string? SessionId, string? ErrorCode, string? ErrorMessage)> SessionResponses { get; } = [];

  private readonly TaskCompletionSource _firstSessionResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Completes once the app has answered its first session-request — the
  ///     deterministic 'the answer landed' signal a pending-window test needs instead of a
  ///     sleep.</summary>
  public Task FirstSessionResponse => _firstSessionResponse.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

  public void SendSessionResponse(string requestId, string? sessionId, string? errorCode, string? errorMessage)
  {
    SessionResponses.Add((requestId, sessionId, errorCode, errorMessage));
    _ = _firstSessionResponse.TrySetResult();
  }

  public async Task<Result<string>> SendRootTurnAsync(RootTurnRequest request,
      Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    LastRequest = request;
    TurnsSent++;
    _ = _started.TrySetResult();

    if (HoldOpen)
    {
      try
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return Result.Failure<string>(new DomainError("TurnCancelled", "the turn was stopped."));
      }
    }

    if (FailWith is { } failure)
    {
      return Result.Failure<string>(failure);
    }

    foreach (string delta in deltas)
    {
      onEvent(new RootTurnEvent(request.TurnId, "delta", delta, request.Generation));
    }

    foreach (RootTurnEvent evt in Script)
    {
      onEvent(evt with { TurnId = request.TurnId, Generation = request.Generation });
    }

    string answer = SettleWith ?? string.Concat(deltas);
    onEvent(new RootTurnEvent(request.TurnId, "settled", answer, request.Generation));
    return Result.Success(answer);
  }
}

/// <summary>A one-skill catalog: enough for the slash path to resolve a name and render the
///     verbatim System line the host must receive.</summary>
internal sealed class FakeSkillCatalog(string name) : ISkillCatalog
{
  public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([Definition()]));

  public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
      => Task.FromResult(Result.Success(Definition()));

  private SkillDefinition Definition() => new(
      name, "A fake skill for the thin-client tests.", "Body of the fake skill.",
      1, SkillSource.BuiltIn, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}

/// <summary>A command runner that answers with a fixed model-facing line, so a test can
///     assert the line queues and rides the next turn.</summary>
internal sealed class FakeCommandRunner : IUserCommandRunner
{
  public Task<Result<CommandRun>> RunAsync(string command, CancellationToken ct = default)
      => Task.FromResult(Result.Success(new CommandRun(7, command, 0, false, "hi",
          DateTimeOffset.UnixEpoch)));
}

/// <summary>A turn runner that records invocation and answers 'in-process'. Any non-zero
///     count is proof the app ran agent work itself.</summary>
internal sealed class RecordingTurnRunner
{
  private int _invocations;

  public int Invocations => Volatile.Read(ref _invocations);

  // IDE0060: parameters required to match the TurnRunner delegate shape; values ignored.
#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
  public Task<Result<string>> RunAsync(SendMessageCommand command, CancellationToken ct,
      TurnCallbacks? callbacks = null, Action<string>? onNotice = null)
  {
    ArgumentNullException.ThrowIfNull(command);
    _ = Interlocked.Increment(ref _invocations);
    return Task.FromResult(Result.Success("in-process answer"));
  }
#pragma warning restore IDE0060, S1172 // Delegate-shape parameters are unused by design.
}
