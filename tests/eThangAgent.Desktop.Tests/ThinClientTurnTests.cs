using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
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
  public static Task<FakeHostSession> StartAsync(IReadOnlyList<string> deltas, bool holdOpen = false)
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
  private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

  /// <summary>Events emitted verbatim after the scripted deltas (the tool/notice kinds).</summary>
  public List<RootTurnEvent> Script { get; } = [];

  /// <summary>When set, the turn fails at the transport instead of settling.</summary>
  public DomainError? FailWith { get; set; }

  public int TurnsSent { get; private set; }

  public RootTurnRequest? LastRequest { get; private set; }

  /// <summary>Completes once a turn is in flight — the deterministic 'busy' signal a
  ///     stop test needs instead of a sleep.</summary>
  public Task TurnStarted => _started.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

  public async Task<Result<string>> SendRootTurnAsync(RootTurnRequest request,
      Action<RootTurnEvent> onEvent, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(onEvent);
    LastRequest = request;
    TurnsSent++;
    _ = _started.TrySetResult();

    if (holdOpen)
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

    string answer = string.Concat(deltas);
    onEvent(new RootTurnEvent(request.TurnId, "settled", answer, request.Generation));
    return Result.Success(answer);
  }
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
