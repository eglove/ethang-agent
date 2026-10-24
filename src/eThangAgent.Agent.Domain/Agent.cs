using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

public class Agent(IModelProvider provider, Conversation conversation, ModelConfig config,
    IToolRegistry tools, AgentOptions? options = null)
{
  /// <summary>Bounded auto-continuations per turn when a response ends with
  ///     <see cref="FinishReason.Length"/>.</summary>
  public const int DefaultMaxAutoContinuations = 8;

  /// <summary>Default utilization percent that trips the compactor. The compaction
  ///     policy's small-window ceiling: <see cref="CompactionPolicy.For"/> derives every
  ///     pressure from the context window, and windows at or below
  ///     <see cref="CompactionPolicy.SmallWindowMax"/> keep this ceiling.</summary>
  public const double DefaultCompactionThreshold = 80.0;

  /// <summary>Verbatim prefix of the one-time soft reminder the loop appends when
  ///     utilization first reaches the policy's soft level: names the pressure, names the
  ///     tool, names the ceiling. Never repeats until the pressure cycle resets.</summary>
  public const string SoftReminderPrefix = "[context:";

  /// <summary>How many times above the conversation's own character estimate a
  ///     provider-reported input-token count may sit and still be believed. Server-side
  ///     tool calls (OpenRouter web_search) ride the provider's token accounting with
  ///     their search pages but never enter the message history, so a report far beyond
  ///     estimate x this factor is inflation, not context — compaction must not fire on
  ///     it. 4 is beyond any legitimate ratio: a real request's system prompt and tool
  ///     definitions are a fraction of a genuinely full context, never 4x it.</summary>
  public const double MaxTrustedUsageOverEstimate = 4.0;

  /// <summary>Appended as a System message after a length-truncated assistant response.
  ///     Verbatim contract: the model must resume exactly where it stopped.</summary>
  public const string ContinuationPrompt =
      "[Your previous message was cut off by the output limit. Continue exactly where you stopped; do not repeat earlier text.]";

  /// <summary>Appended as a System message after a length-truncated response that
  ///     carried NO visible text: the output budget was consumed before any content
  ///     surfaced (typically hidden reasoning). The generic continuation prompt is
  ///     wrong here — there is nothing to resume — and it invites a full
  ///     re-derivation, i.e. another truncated multi-minute stall (2026-10-08:
  ///     subagents sat in consecutive 3-10 minute provider calls producing empty
  ///     responses). This nudge names the situation and demands the short path.</summary>
  public const string EmptyContinuationPrompt =
      "[Your previous response hit the output limit with no visible text — the budget was consumed before any content surfaced, likely by hidden reasoning. Do NOT re-derive your previous work: give the answer or next tool call directly and concisely, skipping any long reasoning pass.]";

  /// <summary>Error code returned (as a Result failure, never an exception) when the turn's
  ///     token fires mid-loop.</summary>
  public const string TurnCancelledCode = "TurnCancelled";

  /// <summary>Error code returned (as a Result failure, never an exception) when the
  ///     repeat guard ends the turn: the same call repeated past the critical error
  ///     (the 14th breaker-counted call). A child's turn ends and settles back to the
  ///     parent; at root the turn ends and control returns to the user.</summary>
  public const string RepeatGuardStopCode = "RepeatGuardStop";

  /// <summary>Synthetic tool result appended for each tool call left unanswered by an
  ///     interruption. Verbatim contract: tells the model why its calls produced nothing.</summary>
  public const string InterruptedToolResult = "[turn interrupted by the user; this call never ran]";

  /// <summary>Verbatim prefix of the System message appended when the provider fails
  ///     mid-turn: the persisted transcript records WHY the turn stopped instead of
  ///     ending on a bare tool result (the failure was previously UI-notice only).</summary>
  public const string TurnFailedPrefix = "[turn failed] ";

  /// <summary>Verbatim final System line appended when the repeat guard ends the turn:
  ///     the conversation ends on WHY the turn stopped, never on a bare tool result.</summary>
  public const string RepeatGuardStopLine = "[repeat guard] turn ended: the same call repeated past the critical error";

  /// <summary>Verbatim prefix a tool result carries when the conversation shrank
  ///     during this turn (the context_edit tool's contract). The loop rests
  ///     auto-compaction for the rest of the turn; persistence replaces the
  ///     transcript instead of appending the turn's slice.</summary>
  public const string ContextShrinkSentinel = "[context: shrank";

  /// <summary>Provider error code for a context-window overflow. The provider ACL maps
  ///     the wire-level fact (an HTTP 400 body naming the context-length fault) to this
  ///     domain code; the domain reacts to the code and never knows HTTP exists. On it,
  ///     the loop runs one FORCED compaction (bypassing the utilization threshold) and
  ///     re-sends the same request once.</summary>
  public const string ContextWindowExceededCode = "ContextWindowExceeded";

  /// <summary>Failure code returned when an overflow recovery cannot run because no
  ///     compactor is wired: retrying without compaction would fail identically.</summary>
  public const string CompactionUnavailableCode = "CompactionUnavailable";

  private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
  private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));
  private readonly ISystemPromptProvider? _systemPrompt = options?.SystemPrompt;
  private readonly IContextMonitor? _contextMonitor = options?.ContextMonitor;
  private readonly IContextCompactor? _contextCompactor = options?.ContextCompactor;
  private readonly IAgentHeartbeat? _heartbeat = options?.Heartbeat;
  private readonly IAgentEvents? _events = options?.Events;
  private readonly int _maxAutoContinuations = options?.MaxAutoContinuations ?? DefaultMaxAutoContinuations;
  private readonly string? _sessionId = options?.SessionId;
  private readonly Action<string>? _onStreamActivity = options?.OnStreamActivity;
  private readonly IServerToolImageStore? _serverToolImages = options?.ServerToolImages;
  private readonly IAgentEventLog? _eventLog = options?.EventLog;
  private readonly ToolRepeatGuard _repeatGuard = new();
  private readonly ReadFreshnessLedger _readLedger = new();

  public Conversation Conversation { get; } = conversation ?? throw new ArgumentNullException(nameof(conversation));

  private static IDisposable SubscribeSink(Conversation conversation, IChildTranscriptStore sink)
  {
    return conversation.Subscribe(
        onAdded: message => _ = PersistQuietlyAsync(sink, message),
        onReplaced: () => _ = ReplaceQuietlyAsync(conversation, sink));
  }

  /// <summary>Incremental transcript persistence: when a sink is wired, every
  ///     conversation mutation flows to it at the safe point where the message is
  ///     added. A sink fault is swallowed (best-effort persistence): the loop must
  ///     never crash because persistence hiccupped — the terminal flush retries.</summary>
  private static async Task PersistQuietlyAsync(IChildTranscriptStore sink, Message message)
  {
    try
    {
      await sink.AppendAsync(message).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Swallowed deliberately: best-effort incremental persistence; the terminal
      // flush retries anything left pending.
    }
  }

  private static async Task ReplaceQuietlyAsync(Conversation conversation, IChildTranscriptStore sink)
  {
    try
    {
      await sink.ReplaceAsync(conversation.Messages).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Swallowed deliberately: see PersistQuietlyAsync.
    }
  }
  public ModelConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

  // Incremental transcript persistence: subscribed here (after the conversation
  // property initializer) so the subscription sees no earlier state; seed messages
  // from resume hydration were persisted by their original run and are not re-sent.
  /// <summary>The live transcript subscription handle; null when no sink is wired.
  ///     Observable, and DISPOSABLE by whoever owns this agent's lifetime: a host that
  ///     rebuilds an agent over the same conversation (the root holder) retires the old
  ///     subscription, because two live subscriptions would push every message twice.
  ///     Non-null means the loop persists incrementally.</summary>
  internal IDisposable? TranscriptSubscription { get; } = SubscribeTranscript(conversation, options?.TranscriptSink);

  /// <summary>Subscribes the wired sink and, when it is the ROOT's sink, registers the lease
  ///     with it so the sink retires the previous build's subscription. Roots are rebuilt on
  ///     every model change over the same conversation; a second live subscription would push
  ///     every message twice. Child sinks are per-run and need no retirement.</summary>
  private static IDisposable? SubscribeTranscript(Conversation conversation, IChildTranscriptStore? sink)
  {
    if (sink is null)
    {
      return null;
    }

    IDisposable lease = SubscribeSink(conversation, sink);
    if (sink is RootTranscriptSink rootSink)
    {
      rootSink.TrackSubscription(lease);
    }

    return lease;
  }

  /// <summary>Identity of this agent. Roots generate one on construction; spawned children carry their persisted id.</summary>
  public AgentId Id { get; } = options?.Id ?? AgentId.NewId();

  /// <summary>Depth in the spawn tree. Root agents are depth 0; children run at parent depth + 1.</summary>
  public int Depth { get; } = options?.Depth ?? 0;

  /// <summary>Tool calls executed during the most recent SendMessage; 0 when the turn ended without any.</summary>
  public int LastTurnToolCalls { get; private set; }

  /// <summary>True when a tool result carried <see cref="ContextShrinkSentinel"/>
  ///     during the most recent SendMessage; reset at the start of each turn.</summary>
  public bool ShrankThisTurn { get; private set; }

  private bool _shrankThisTurnObserved;

  /// <summary>One-time soft reminder state: true from the iteration the reminder was
  ///     appended until the pressure cycle resets (a successful compaction or an
  ///     observed context shrink). A failed compaction does NOT reset it.</summary>
  private bool _softReminderShown;

  /// <summary>ONE HANDOFF DECISION PER TURN (issue #234 fix round): set when a
  ///     compaction outcome IS a handoff, read by ManageContextAsync to block the
  ///     auto-compact backstop for the remainder of the turn. A handoff does NOT shrink
  ///     the conversation, so utilization stays above the ceiling — without this flag
  ///     every subsequent iteration would re-enter the compactor (a fresh summarizer
  ///     LLM call per iteration). Same shape as <c>_shrankThisTurnObserved</c>: a
  ///     turn-local fact set deep in the loop, consumed at the iteration boundary,
  ///     reset at the turn's start.</summary>
  private bool _handoffDecidedThisTurn;

  /// <summary>True when the last successful compaction's outcome WAS a handoff
  ///     (issue #234): the composition layer reads this after the turn ends and launches
  ///     the new session with the outcome's summary as its seed. One-shot per
  ///     compaction: the composition resets it via <see cref="ClearHandoffPending"/>
  ///     after it acts. The handoff acts at the turn's END, not mid-decision: the
  ///     turn continues normally after the compaction.</summary>
  public bool HandoffPending { get; private set; }

  /// <summary>The handoff summary the last handoff outcome carried (issue #234): the
  ///     seed the new session launches with. Null when no handoff is pending. Cleared
  ///     with <see cref="ClearHandoffPending"/>.</summary>
  public string? HandoffSummary { get; private set; }

  /// <summary>Clears <see cref="HandoffPending"/> after the composition has acted on it
  ///     (launched the new session with the compaction's summary as its seed).</summary>
  public void ClearHandoffPending()
  {
    HandoffPending = false;
    HandoffSummary = null;
  }

  /// <summary>TEST SEAM (issue #234 fix round): drives the handoff-pending state
  ///     directly, so the host's emit tests can stage a pending handoff without running
  ///     a compaction. Production never calls this — the flag is set only from a
  ///     compaction outcome.</summary>
  internal void SetHandoffForTests(bool pending, string? summary)
  {
    HandoffPending = pending;
    HandoffSummary = summary;
  }

  /// <summary>
  /// Runs one user turn through the provider/tool loop. Content deltas stream out through
  /// <see cref="TurnCallbacks.OnContentDelta"/> exactly as the provider emits them — every
  /// iteration, interstitial text between tool calls included — and
  /// <see cref="TurnCallbacks.OnIterationEnd"/> fires once after each provider response so
  /// observers can separate iterations. All callbacks are optional: providers without
  /// streaming support simply never invoke the delta callback, and the returned result is
  /// identical either way. Callbacks may fire on arbitrary threads; observers must marshal
  /// to their own context.
  ///
  /// Steering: when <paramref name="inbox"/> is supplied, messages posted to it while the
  /// turn runs are drained as User messages — once before the turn starts (leftovers from a
  /// previous turn) and once at each iteration boundary after the cancellation check. They
  /// are never drained between an assistant tool-call message and its results.
  ///
  /// Interruption: cancellation is a Result failure, not a crash. When <paramref name="ct"/>
  /// fires mid-turn the conversation is repaired first — every unanswered tool call receives
  /// the synthetic <see cref="InterruptedToolResult"/> so history stays protocol-valid — and
  /// the method returns Failure(TurnCancelled).
  /// </summary>
  public async Task<Result<string>> SendMessage(string text,
      TurnCallbacks? callbacks = null,
      IAgentInbox? inbox = null,
      IReadOnlyList<MessagePart>? imageParts = null,
      CancellationToken ct = default)
  {
    try
    {
      return await RunTurnAsync(text, callbacks, inbox, imageParts, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      RepairInterruptedToolCalls();
      return Result.Failure<string>(new DomainError(TurnCancelledCode, RuntimeErrors.TurnCancelled));
    }
  }

  /// <summary>Turn-local loop state: the bounded-retry counters an overflow recovery
  ///     or an auto-continuation set for the REMAINDER of the turn (never carried
  ///     between turns).</summary>
  private sealed class TurnState
  {
    // Auto-continuations used by this turn only: reset at the turn's start, never
    // carried between turns. Properties, not fields: the repo's naming rules prefix
    // every field — including nested-class ones.
    public int Continuations { get; set; }
    // Turn identity for the reasoning telemetry channel (issue #263): a fresh id per
    // turn, so a resumed conversation's traces never collide with an earlier turn's.
    public Guid TurnId { get; init; } = Guid.NewGuid();
    // 0-based provider call within this turn: the other half of a trace's identity.
    public int Iteration { get; set; }
    // Overflow recovery is turn-local: one forced compaction and one re-send per
    // turn — a second overflow fails the turn, bounded.
    public bool OverflowRecovered { get; set; }
    public bool AutoCompactBlocked { get; set; }
  }

  /// <summary>The turn loop proper (everything <see cref="SendMessage"/> advertises in
  ///     its contract comment): drain, iterate model calls, execute tool calls, and
  ///     manage context pressure, until the model answers without tool calls.</summary>
  private async Task<Result<string>> RunTurnAsync(string text, TurnCallbacks? callbacks,
      IAgentInbox? inbox, IReadOnlyList<MessagePart>? imageParts, CancellationToken ct)
  {
    LastTurnToolCalls = 0;
    ShrankThisTurn = false;
    _shrankThisTurnObserved = false;
    _handoffDecidedThisTurn = false;
    _repeatGuard.Reset();
    _readLedger.Reset();
    TurnState state = new();
    DrainInbox(inbox);
    Conversation.AddUserMessage(text, imageParts);
    Beat();
    // No iteration cap by design: the loop runs until the model answers without
    // tool calls. Termination is the model's job — but cancellation is checked
    // every round, because nothing else in the loop is obliged to observe ct
    // (fakes, cached providers, and instant tools may never see it).
    while (true)
    {
      ct.ThrowIfCancellationRequested();
      Beat();
      DrainInbox(inbox);
      await ManageContextAsync(state, callbacks, ct).ConfigureAwait(false);
      // Snapshot, not view: Conversation.Messages is a live wrapper over the growing
      // list, so handing it out directly would let every consumer of this request
      // (retries, logging, tests) read messages added by later iterations.
      ModelRequest request = new(
          [.. Conversation.Messages], _tools.Definitions, _systemPrompt?.Build(), _sessionId);
      // Stream-activity stamping: every fragment (content or reasoning) proves the
      // provider stream is alive. The stamp rides the option, not the turn callbacks,
      // so children get it without a UI surface. Null option: byte-identical legacy.
      Action<string>? contentDelta = WrapStreamStamp(callbacks?.OnContentDelta, "content");
      Action<string>? reasoningDelta = WrapStreamStamp(callbacks?.OnReasoningDelta, "reasoning");
      Result<ModelResponse> result = await _provider.SendStreamingAsync(Config, request,
          contentDelta, reasoningDelta, ct).ConfigureAwait(false);
      if (!result.IsSuccess)
      {
        Result<string>? failure = await HandleProviderFailureAsync(result.Error, state, callbacks, ct)
            .ConfigureAwait(false);
        if (failure is { } failureOutcome)
        {
          return failureOutcome;
        }

        continue; // overflow recovery ran: re-send
      }

      // The reasoning trace is recorded BEFORE the iteration advances: the safe point
      // is the moment the response exists, so a crash later in the turn still leaves
      // every completed iteration's trace persisted (issue #263).
      await RecordReasoningAsync(result.Value, state).ConfigureAwait(false);
      state.Iteration++;
      callbacks?.OnIterationEnd?.Invoke();

      Result<string>? handled = await HandleSuccessAsync(result.Value, state, callbacks, ct).ConfigureAwait(false);
      if (handled is { } handledOutcome)
      {
        return handledOutcome;
      }
    }
  }

  /// <summary>The successful-response phase: usage accounting, server-tool surfacing,
  ///     the no-tool-call finish policy, or the tool-call batch execution. Returns the
  ///     turn's terminal Result, or null to continue the loop.</summary>
  private async Task<Result<string>?> HandleSuccessAsync(ModelResponse response, TurnState state,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    // Snapshot, not view: Conversation.Messages is a live wrapper over the growing
    // list, so handing it out directly would let every consumer of this request
    // (retries, logging, tests) read messages added by later iterations.
    ReportUsage(new ModelRequest([.. Conversation.Messages], _tools.Definitions,
        _systemPrompt?.Build(), _sessionId), response, callbacks);
    await SurfaceServerToolCallsAsync(response, callbacks, ct).ConfigureAwait(false);
    if (response.ToolCalls.Count == 0)
    {
      return FinishWithoutToolCalls(response, state, callbacks);
    }

    Conversation.AddAssistantMessage(response.Content ?? "",
        [.. response.ToolCalls.Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))]);
    return await ExecuteToolCallsAsync(response.ToolCalls, callbacks, ct).ConfigureAwait(false)
        ? null
        : Result.Failure<string>(new DomainError(RepeatGuardStopCode, RuntimeErrors.RepeatGuardStop));
  }

  /// <summary>The per-iteration context-pressure phase: the shrink observation, the
  ///     threshold compaction, and the turn-local auto-compact backstop.</summary>
  private async Task ManageContextAsync(TurnState state, TurnCallbacks? callbacks, CancellationToken ct)
  {
    if (state.AutoCompactBlocked)
    {
      return;
    }

    bool shrinkObserved = SetShrankIfObserved();
    if (shrinkObserved)
    {
      callbacks?.OnContextShrunk?.Invoke();
    }

    bool compacted = !shrinkObserved && await TryCompactIfNeededAsync(callbacks, ct).ConfigureAwait(false);
    // A handoff decision blocks the backstop too (one decision per turn): the
    // conversation did not shrink, so utilization would re-fire every iteration.
    state.AutoCompactBlocked = compacted || shrinkObserved || _handoffDecidedThisTurn;
  }

  /// <summary>A failed provider call: the caller-stop check, the one-shot overflow
  ///     recovery, or the transcript turn-failure line. Returns the turn's terminal
  ///     Result, or null when the loop may re-send (overflow recovery ran).</summary>
  private async Task<Result<string>?> HandleProviderFailureAsync(DomainError error, TurnState state,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    // A caller stop that landed DURING the provider call arrives here as a provider
    // failure: the ACL maps every OperationCanceledException to ProviderTimeout and
    // cannot tell a caller stop from a genuine timeout. Our own ct is the authority
    // for who cancelled — take the interruption path (repair + TurnCancelled), never
    // a "[turn failed] ProviderTimeout" line for the user's own stop.
    if (ct.IsCancellationRequested)
    {
      RepairInterruptedToolCalls();
      return Result.Failure<string>(new DomainError(TurnCancelledCode, RuntimeErrors.TurnCancelled));
    }

    // Context-window overflow recovery: the provider ACL's distinct code is the
    // seam — the domain knows nothing about HTTP. One forced compaction (the
    // utilization check cannot see the drift that just overflowed) and one
    // re-send of the SAME request; a second overflow fails the turn.
    if (error.Code is ContextWindowExceededCode && !state.OverflowRecovered)
    {
      Result<string>? recovered = await RecoverFromContextOverflowAsync(error, callbacks, ct).ConfigureAwait(false);
      if (recovered is null)
      {
        state.OverflowRecovered = true;
        // The monitor's utilization report predates the overflow — re-running the
        // threshold check on it would compact again on stale state. The turn rests
        // the backstop here exactly as a successful threshold compaction does.
        state.AutoCompactBlocked = true;
        return null;
      }

      return recovered;
    }

    // The failure becomes part of the transcript: a turn that dies on a provider
    // error must not leave the conversation ending on a bare tool result with no
    // record of why — the model (and any resumed session) reads this line.
    string failure = $"{TurnFailedPrefix}Error [{error.Code}]: {error.Message}";
    Conversation.AddSystemMessage(failure);
    callbacks?.OnSystemMessage?.Invoke(failure);
    return Result.Failure<string>(error);
  }

  /// <summary>Records one iteration's reasoning text as TELEMETRY (issue #263): an
  ///     append-only event row keyed by (agent, turn, iteration). Runs at the safe point
  ///     where the response exists, with <see cref="CancellationToken.None"/> — the trace
  ///     of a turn that is about to be cancelled is exactly the trace worth keeping.
  ///     <para>A provider that streamed no reasoning records nothing (an absent trace is
  ///     a different fact from an empty one), and a persistence fault is SWALLOWED: the
  ///     established best-effort discipline — the turn must never fail because its
  ///     telemetry hiccupped. Nothing here reads back into the request, so the model's
  ///     next request is byte-identical whether or not this ran.</para></summary>
  private async Task RecordReasoningAsync(ModelResponse response, TurnState state)
  {
    if (_eventLog is null || string.IsNullOrEmpty(response.Reasoning))
    {
      return;
    }

    try
    {
      _ = await _eventLog.AppendEventAsync(
          new AgentReasoningRecorded(Id, DateTimeOffset.UtcNow, state.TurnId, state.Iteration,
              response.Reasoning),
          CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Swallowed deliberately: telemetry is best-effort, never turn-failing.
    }
  }

  /// <summary>Beats the wired heartbeat at a loop safe point, or no-op when none is
  ///     wired (legacy wiring beats nothing: byte-identical behavior).</summary>
  /// <summary>Consumes the mid-turn shrink observation: reads whether a tool
  ///     result carried the sentinel and propagates it to the public property.</summary>
  private bool SetShrankIfObserved()
  {
    if (_shrankThisTurnObserved)
    {
      ShrankThisTurn = true;
    }

    return _shrankThisTurnObserved;
  }

  private void Beat()
  {
    _heartbeat?.Beat(Id);
    _events?.Publish(new ChildProgressEvent(Id, DateTimeOffset.UtcNow, ChildPhase.ModelCall, "iteration"));
  }

  /// <summary>Publishes a progress event to the wired event stream, or no-op when none
  ///     is wired. Labels are short phase tags, never content (D5).</summary>
  private void PublishProgress(ChildPhase phase, string label)
      => _events?.Publish(new ChildProgressEvent(Id, DateTimeOffset.UtcNow, phase, label));

  /// <summary>Pressure phase of an iteration: when a compactor and monitor are wired,
  ///     derives the compaction policy from the monitor's context window (per iteration,
  ///     so a mid-session window change re-derives automatically) and acts on the
  ///     utilization report. At or above the policy's soft level (and below its ceiling)
  ///     the ONE-TIME soft reminder is appended — a System message naming the pressure,
  ///     the context_trim tool, and the ceiling; it never repeats until the pressure
  ///     cycle resets. At the ceiling the compactor runs: success fires
  ///     <see cref="TurnCallbacks.OnCompacted"/> and re-fires the context snapshot;
  ///     failure appends a System notice telling the model why nothing changed and
  ///     returns true so a broken compactor stops being retried this turn.</summary>
  private async Task<bool> TryCompactIfNeededAsync(TurnCallbacks? callbacks, CancellationToken ct)
  {
    if (_contextCompactor is null || _contextMonitor is not { } monitor)
    {
      return false;
    }

    // Null utilization (nothing reported yet) fires neither reminder nor compaction —
    // lifted >= semantics: the original guard read utilization >= threshold, under
    // which null fell through.
    if (monitor.Status.UtilizationPercent is not { } utilization)
    {
      return false;
    }

    // Inflation guard FIRST: a provider-reported input-token count far beyond what the
    // conversation itself could hold is server-tool accounting, not context. It fires
    // NEITHER the reminder NOR the compactor — running the compactor on it fails
    // spuriously (CompactionImpossible on a tiny
    // conversation); skipping quietly is correct — the next honest report compacts.
    if (UsageInflated(monitor))
    {
      return false;
    }

    // The policy is derived per iteration from the monitor's window: a mid-session
    // model change (root rebuild) re-derives automatically.
    CompactionPolicy policy = CompactionPolicy.For(monitor.Status.ContextWindow);
    ShowSoftReminderIfPressured(policy, utilization, callbacks);
    if (utilization < policy.CeilingPercent)
    {
      return false;
    }

    Result<CompactionOutcome> compacted =
        await _contextCompactor.CompactAsync(Conversation, Config, ct).ConfigureAwait(false);
    if (compacted.IsSuccess)
    {
      // History was replaced: nothing held before is held now; a successful
      // compaction also starts a new pressure cycle.
      _readLedger.Reset();
      _softReminderShown = false;
      NotifyCompacted(callbacks, compacted.Value);
      // The handoff decision rides the OUTCOME (issue #234): a handoff outcome means the
      // conversation was NOT compacted and the summary seeds a new session. Set the
      // pending flag and let the turn continue normally - the handoff acts at the turn's
      // END, where the composition reads the flag and launches the new session. An
      // ordinary outcome leaves the flag untouched.
      if (compacted.Value.IsHandoff)
      {
        HandoffPending = true;
        HandoffSummary = compacted.Value.HandoffSummary;
        // One decision per turn: the conversation did NOT shrink, so utilization stays
        // above the ceiling and the backstop would re-enter the compactor on every
        // remaining iteration (a fresh summarizer call each time). Block it for the
        // remainder of the turn — the handoff acts at the turn's END.
        _handoffDecidedThisTurn = true;
      }

      return false;
    }

    // Graceful degradation: a System notice tells the model why nothing changed;
    // the turn-local flag (this return value) keeps a broken compactor from spamming.
    string notice =
        $"[Context compaction failed: {compacted.Error.Code} {compacted.Error.Message}; continuing without compaction.]";
    Conversation.AddSystemMessage(notice);
    callbacks?.OnSystemMessage?.Invoke(notice);
    return true;
  }

  /// <summary>Inflation guard: a provider-reported input-token count far beyond what
  ///     the conversation itself could hold is server-tool accounting, not context. It
  ///     fires NEITHER the reminder NOR the compactor — running the compactor on it
  ///     fails spuriously (CompactionImpossible on a tiny conversation); skipping
  ///     quietly is correct — the next honest report compacts.</summary>
  private bool UsageInflated(IContextMonitor monitor)
  {
    long estimateChars = _systemPrompt?.Build().Length ?? 0;
    foreach (Message message in Conversation.Messages)
    {
      estimateChars += message.Content.Length;
      if (message.ToolCalls is { Count: > 0 } calls)
      {
        estimateChars += calls.Sum(call => call.Arguments.Length + call.Name.Length);
      }
    }

    long estimateTokens = estimateChars / ContextEvictionPolicy.CharsPerTokenEstimate;
    return monitor.Status.LastInputTokens is { } reported
        && reported > (long)(estimateTokens * MaxTrustedUsageOverEstimate);
  }

  /// <summary>The ONE-TIME soft reminder at or above the policy's soft level (and
  ///     below its ceiling): a System message naming the pressure, the context_trim
  ///     tool, and the ceiling; it never repeats until the pressure cycle resets.</summary>
  private void ShowSoftReminderIfPressured(CompactionPolicy policy, double utilization, TurnCallbacks? callbacks)
  {
    if (utilization < policy.SoftReminderPercent || utilization >= policy.CeilingPercent || _softReminderShown)
    {
      return;
    }

    _softReminderShown = true;
    string reminder = $"{SoftReminderPrefix} {utilization:0}% full — trim tool results you no longer need with context_trim; a forced cleanup runs at {policy.CeilingPercent:0}%.]";
    Conversation.AddSystemMessage(reminder);
    callbacks?.OnSystemMessage?.Invoke(reminder);
  }

  /// <summary>The observer surface every successful compaction fires — identical for
  ///     the threshold backstop and the forced overflow recovery: ledger reset,
  ///     pressure-cycle reset, the OnCompacted callback, the summary line the
  ///     compaction left in the conversation surfaced verbatim, and the re-fired
  ///     context snapshot.</summary>
  private void NotifyCompacted(TurnCallbacks? callbacks, CompactionOutcome outcome)
  {
    // The summary message the compaction left in the conversation is surfaced
    // verbatim: nothing the agent receives stays hidden from the host surface
    // (live parity with the resume replay, which restores it as a system
    // entry). A HANDOFF outcome did NOT compact the conversation - there is no
    // summary message in it to surface, and none was received, so none is fired
    // (the summary travels on the outcome, to the host's handoff emit).
    if (!outcome.IsHandoff && Conversation.Messages.FirstOrDefault(m => m.IsSummary) is { } summary)
    {
      callbacks?.OnSystemMessage?.Invoke(summary.Content);
    }

    callbacks?.OnCompacted?.Invoke(outcome);
    ReportUsageFromMonitor(callbacks);
  }

  /// <summary>Forced compaction after a ContextWindowExceeded provider failure, bypassing
  ///     the utilization threshold: the threshold reads the LAST request's usage report —
  ///     a snapshot that cannot see the tool-result drift that just overflowed the window.
  ///     The eviction plan is built from the current messages directly, against the serving
  ///     model's context window, through the same <see cref="IContextCompactor"/> seam the
  ///     80% backstop uses. Null means recovery ran (compaction succeeded or no compactor
  ///     is wired) and the caller may re-send the SAME request once; a non-null result is
  ///     the turn's terminal failure — the compaction error is the actionable cause, so it
  ///     surfaces in both the returned Result and the transcript's turn-failure line.</summary>
  private async Task<Result<string>?> RecoverFromContextOverflowAsync(DomainError overflowError,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    if (_contextCompactor is null)
    {
      // Nothing to compact with: re-sending would fail identically. The transcript
      // still records the overflow (the model and any resumed session read this line).
      string line = $"{TurnFailedPrefix}Error [{CompactionUnavailableCode}]: no compactor is wired; cannot recover from a context-window overflow. (underlying: Error [{overflowError.Code}]: {overflowError.Message})";
      Conversation.AddSystemMessage(line);
      callbacks?.OnSystemMessage?.Invoke(line);
      return Result.Failure<string>(new DomainError(CompactionUnavailableCode,
          overflowError.Message));
    }

    Beat();
    PublishProgress(ChildPhase.ModelCall, "overflow-recovery");
    // ALWAYS IN PLACE (issue #234 fix round): the handoff is a ceiling-path decision
    // only. A handoff here would skip the compaction and re-send the identical
    // oversized request into the same overflow — the turn would fail with no handoff
    // offered. The in-place shape is the recovery's contract.
    Result<CompactionOutcome> compacted =
        await _contextCompactor.CompactInPlaceAsync(Conversation, Config, ct).ConfigureAwait(false);
    if (!compacted.IsSuccess)
    {
      string line = $"{TurnFailedPrefix}Error [{compacted.Error.Code}]: {compacted.Error.Message} (underlying: Error [{overflowError.Code}]: {overflowError.Message})";
      Conversation.AddSystemMessage(line);
      callbacks?.OnSystemMessage?.Invoke(line);
      return Result.Failure<string>(compacted.Error);
    }

    // Same observer surface as the threshold backstop.
    _readLedger.Reset();  // history was replaced: nothing held before is held now
    _softReminderShown = false;  // a successful compaction starts a new pressure cycle
    NotifyCompacted(callbacks, compacted.Value);
    return null;
  }

  /// <summary>Final-response policy for an iteration whose answer carries no tool calls.
  ///     A length-truncated answer is not final: the partial message stays in history,
  ///     the model is nudged to continue (bounded — a pathological model cannot spin
  ///     forever; leniency with a visible cap, never a silent retry), and null is
  ///     returned so the loop continues. A complete answer is appended and returned.</summary>
  private Result<string>? FinishWithoutToolCalls(ModelResponse response, TurnState state,
      TurnCallbacks? callbacks)
  {
    string content = response.Content ?? "";
    if (response.FinishReason is not FinishReason.Length)
    {
      Conversation.AddAssistantMessage(content);
      if (HandoffPending)
      {
        // The turn ends here: surface the handoff decision as a system line (the
        // transcript records WHY the conversation continues elsewhere) and end the
        // turn with the normal terminal Result. The actual session launch is NOT
        // here - the Agent Domain cannot reference the port; the composition reads
        // HandoffPending after the turn ends and acts on it.
        string handoffLine = "[context handoff: the conversation continues in a new session.]";
        Conversation.AddSystemMessage(handoffLine);
        callbacks?.OnSystemMessage?.Invoke(handoffLine);
      }

      return Result.Success(content);
    }

    if (state.Continuations >= _maxAutoContinuations)
    {
      return Result.Failure<string>(new DomainError("MaxOutputContinuations",
          $"Output limit reached {_maxAutoContinuations + 1} times without a complete answer."));
    }

    state.Continuations++;
    Conversation.AddAssistantMessage(content);
    // An empty truncated response gets the empty-specific nudge: 'continue exactly
    // where you stopped' is meaningless when nothing surfaced, and it invites a
    // full re-derivation — the multi-minute empty-stall shape.
    string continuationPrompt = string.IsNullOrWhiteSpace(content)
        ? EmptyContinuationPrompt
        : ContinuationPrompt;
    Conversation.AddSystemMessage(continuationPrompt);
    callbacks?.OnSystemMessage?.Invoke(continuationPrompt);
    return null;
  }

  /// <summary>Re-fires the context snapshot after a compaction shrank the conversation:
  ///     utilization dropped, so the next threshold decision reads fresh state.</summary>
  private void ReportUsageFromMonitor(TurnCallbacks? callbacks)
  {
    if (_contextMonitor is { } monitor)
    {
      callbacks?.OnContextUpdate?.Invoke(new ContextSnapshot(monitor.Status, monitor.Breakdown));
    }
  }

  /// <summary>Forwards the provider-scored usage of a completed call to the context
  ///     monitor together with the request's composition (character sizes of its three
  ///     cost buckets). Monitor absent (legacy wiring) or usage unreported → no-op: the
  ///     loop's decisions never read usage directly, only the monitor's status.</summary>
  private void ReportUsage(ModelRequest request, ModelResponse response, TurnCallbacks? callbacks)
  {
    if (_contextMonitor is null || response.Usage is not { } usage)
    {
      return;
    }

    int systemPromptChars = request.SystemPrompt?.Length ?? 0;
    long toolChars = request.Tools is null ? 0 : request.Tools.Sum(ToolDefinitionChars);
    _contextMonitor.OnRequestUsage(usage,
        new ContextComposition(systemPromptChars, MessageChars(request.Messages), toolChars));
    callbacks?.OnContextUpdate?.Invoke(new ContextSnapshot(_contextMonitor.Status, _contextMonitor.Breakdown));
  }

  /// <summary>The message bucket of the UI character breakdown.</summary>
  private static long MessageChars(IReadOnlyList<Message> messages)
  {
    long messageChars = 0;
    foreach (Message message in messages)
    {
      messageChars += message.Content.Length;
      if (message.ToolCalls is { Count: > 0 } calls)
      {
        messageChars += calls.Sum(call => call.Arguments.Length + call.Name.Length);
      }

      // Image base64 lengths join the message bucket of the UI character breakdown
      // only. Provider-scored token usage remains the sole decision input; the wire
      // serializes images through provider-specific parts, not this estimate.
      if (message.Parts is { Count: > 0 } parts)
      {
        messageChars += parts.OfType<MessagePart.ImagePart>().Sum(p => p.Base64Data.Length);
      }
    }

    return messageChars;
  }

  /// <summary>Advertised-contract size of one tool definition, in characters. A named
  ///     method rather than lambdas: implicit lambda parameter types here force the
  ///     compiler into 100+ bindings per call site (CS9236) while the explicit-typed
  ///     form trips the simplify-lambda style rule (IDE0350) — loops satisfy both.</summary>
  private static long ToolDefinitionChars(ToolDefinition tool)
  {
    long length = tool.Name.Length + tool.Description.Length;
    foreach (ToolParameter parameter in tool.Parameters)
    {
      length += parameter.Name.Length + parameter.Description.Length;
    }

    foreach (string required in tool.RequiredParameters)
    {
      length += required.Length;
    }

    return length;
  }

  /// <summary>Runs each requested tool call in order, appending its result to the
  ///     conversation and reporting the call and its summary to the observers.
  ///     Returns false when the repeat guard ended the turn (the call after a critical
  ///     error): the remaining calls of the batch get the synthetic
  ///     <see cref="InterruptedToolResult"/>, the final system line names why, and the
  ///     caller must stop the turn. The guard's Observe never sees the synthetic
  ///     results — they cannot extend or reset the streaks.</summary>
  private async Task<bool> ExecuteToolCallsAsync(IReadOnlyList<ToolCallRequest> calls,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    for (int i = 0; i < calls.Count; i++)
    {
      ToolCallRequest call = calls[i];
      LastTurnToolCalls++;
      callbacks?.OnToolCall?.Invoke(call.Name, call.Arguments, i + 1, calls.Count);
      GuardOutcome outcome = await RunOneCallAsync(call, ResolveTool(call.Name), callbacks, ct)
          .ConfigureAwait(false);
      if (outcome.EndTurn)
      {
        // The call after a critical error: the turn ends here. Remaining calls of the
        // batch get the synthetic interrupted result (protocol repair — the guard
        // never observes them, so they cannot extend or reset the streaks), a final
        // system line names why, and the caller fails the turn with RepeatGuardStop.
        EndBatchWithInterrupted(calls, i, callbacks);
        return false;
      }
    }

    return true;
  }

  /// <summary>One call's execution phase: execute, record the result, observe the
  ///     repeat guard, and report to the observers.</summary>
  private async Task<GuardOutcome> RunOneCallAsync(ToolCallRequest call, ITool? tool,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    ToolResult toolResult = await ExecuteOneToolAsync(call, tool, ct).ConfigureAwait(false);
    Conversation.AddToolResult(call.Id, toolResult.Content, ToParts(toolResult.Images));
    ObserveShrinkSentinel(toolResult);

    // Call-identity normalization (issue #124): a tool declaring a semantic identity
    // (exec's program body) keys the guard on it; envelope fields the model free-varies
    // (title, timeoutSeconds) reset nothing. Non-declaring tools keep raw arguments.
    // The guard itself prepends the tool name to whatever key it receives. The tool
    // resolved above is reused — a second Find would make a granting registry audit
    // the same denial twice.
    string guardKey = tool is IToolCallIdentifiable identifiable
        && identifiable.CallIdentity(call.Arguments) is { } identity
            ? identity
            : call.Arguments;
    GuardOutcome outcome = _repeatGuard.Observe(call.Name, guardKey, toolResult.IsError, toolResult.Content);
    if (outcome.NudgeLine is { } guardLine)
    {
      Conversation.AddSystemMessage(guardLine);
      callbacks?.OnSystemMessage?.Invoke(guardLine);
    }

    _heartbeat?.Beat(Id);
    PublishProgress(ChildPhase.Draining, "tool-result");
    string summary = SummarizeToolResult(toolResult);
    callbacks?.OnToolResult?.Invoke(call.Name, summary, toolResult.Content, toolResult.IsError,
        toolResult.Title is null ? null : toolResult);
    return outcome;
  }

  /// <summary>A context-shrinking tool result resets the pressure cycle and the
  ///     read ledger: history was replaced, so nothing held before is held now.</summary>
  private void ObserveShrinkSentinel(ToolResult toolResult)
  {
    if (!toolResult.Content.Contains(ContextShrinkSentinel, StringComparison.Ordinal))
    {
      return;
    }

    _shrankThisTurnObserved = true;
    _softReminderShown = false;  // a context shrink starts a new pressure cycle
    _readLedger.Reset();  // history was replaced: nothing held before is held now
  }

  /// <summary>Resolves the call's tool, binding the read ledger to ledger-bound tools
  ///     that carry none yet.</summary>
  private ITool? ResolveTool(string name)
  {
    ITool? tool = _tools.Find(name);
    if (tool is ILedgerBoundTool ledgerTool && !ledgerTool.HasLedger)
    {
      tool = ledgerTool.WithLedger(_readLedger);
    }

    return tool;
  }

  /// <summary>Executes one resolved tool call (or renders the unknown-tool refusal):
  ///     a registry that owns the grant policy explains the refusal in its own words
  ///     (R1.3: GrantViolation, distinguishable from typo); a plain registry renders
  ///     the standard UnknownTool line.</summary>
  private async Task<ToolResult> ExecuteOneToolAsync(ToolCallRequest call, ITool? tool, CancellationToken ct)
  {
    PublishProgress(ChildPhase.ToolExec, "tool:" + call.Name);
    _heartbeat?.Beat(Id);
    return tool is null
        ? new ToolResult((_tools as FilteredToolRegistry)?.ExplainsRefusal(call.Name)
            ?? $"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
        : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct).ConfigureAwait(false);
  }

  /// <summary>The calls after the guard's stop get the synthetic interrupted result
  ///     (protocol repair — the guard never observes them, so they cannot extend or
  ///     reset the streaks), then a final system line names why.</summary>
  private void EndBatchWithInterrupted(IReadOnlyList<ToolCallRequest> calls, int stoppedAt, TurnCallbacks? callbacks)
  {
    for (int j = stoppedAt + 1; j < calls.Count; j++)
    {
      Conversation.AddToolResult(calls[j].Id, InterruptedToolResult);
    }

    string stopLine = RepeatGuardStopLine;
    Conversation.AddSystemMessage(stopLine);
    callbacks?.OnSystemMessage?.Invoke(stopLine);
  }


  /// <summary>Converts a tool result's images into conversation message parts; null
  ///     or empty stays null so text-only results keep the legacy message shape.</summary>
  private static IReadOnlyList<MessagePart>? ToParts(IReadOnlyList<ToolResultImage>? images)
      => images is { Count: > 0 }
          ? [.. images.Select(i => new MessagePart.ImagePart(i.MediaType, i.Base64Data))]
          : null;

  /// <summary>Surfaces the response's server-side tool calls as System lines — one per
  ///     call, verbatim contract "[&lt;tool&gt;] &lt;detail&gt;" (detail omitted when the wire
  ///     carried none), each result URL on its own line beneath (the wire's
  ///     action.sources). Server calls execute inside the provider's response and never
  ///     enter the message history; without this the user's transcript shows nothing.
  ///     The lines also land in the conversation so a resumed session sees them.
  ///     <para>An image-generation call's artifact is PERSISTED first (issue #230): the
  ///     bytes go into the workspace through the wired <see cref="IServerToolImageStore"/>,
  ///     the surfaced line NAMES the saved path, and the image rides the tool-result
  ///     callback so the Desktop renders it through the same surface computer screenshots
  ///     already use. A store failure is a TYPED error on the line — the user must learn
  ///     the artifact could not be saved, never see a silent skip.</para></summary>
  private async Task SurfaceServerToolCallsAsync(ModelResponse response, TurnCallbacks? callbacks,
      CancellationToken ct)
  {
    if (response.ServerToolCalls.Count == 0)
    {
      return;
    }

    foreach (ServerToolCall call in response.ServerToolCalls)
    {
      string line = call.Image is { } image
          ? await SurfaceImageAsync(call, image, callbacks, ct).ConfigureAwait(false)
          : RenderServerToolCall(call);
      Conversation.AddSystemMessage(line);
      callbacks?.OnSystemMessage?.Invoke(line);
    }
  }

  /// <summary>Persists one generated image and renders its surfaced line. The path is the
  ///     artifact's identity in the transcript (the model can read_image it later); the
  ///     image itself rides the tool-result callback for the transcript to render. A
  ///     failed save renders the typed error on the same line, so the transcript records
  ///     WHY the artifact is missing.</summary>
  private async Task<string> SurfaceImageAsync(ServerToolCall call, ServerToolCallImage image,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    if (_serverToolImages is null)
    {
      return RenderServerToolCall(call);
    }

    Result<SavedServerToolImage> saved = await _serverToolImages
        .SaveAsync(image.MediaType, image.Base64Data, ct).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return $"[{call.Tool}] Error [{saved.Error.Code}]: {saved.Error.Message}";
    }

    callbacks?.OnToolResult?.Invoke(call.Tool, saved.Value.RelativePath, saved.Value.RelativePath,
        false, new ToolResult($"saved to {saved.Value.RelativePath}", false, call.Tool,
            [new ToolResultImage(image.MediaType, image.Base64Data)]));
    // The hint is gated on the resolved model's vision capability: a model that cannot
    // read images must not be invited to call read_image on the artifact.
    return Config.AcceptsImageInput
        ? $"[{call.Tool}] {saved.Value.RelativePath} (read_image can open it)"
        : $"[{call.Tool}] {saved.Value.RelativePath}";
  }

  /// <summary>One surfaced line: "[&lt;tool&gt;] &lt;detail&gt;" plus one line per result URL.
  ///     Newlines only when sources exist, so the legacy single-line contract is
  ///     byte-identical when the wire carried none.</summary>
  private static string RenderServerToolCall(ServerToolCall call)
  {
    string header = call.Detail is { } detail ? $"[{call.Tool}] {detail}" : $"[{call.Tool}]";
    return call.Sources.Count == 0
        ? header
        : header + "\n" + string.Join("\n", call.Sources);
  }

  /// <summary>Guard-style early returns: a failed result truncates its content to the
  /// first 77 characters plus an ellipsis; success summarizes as "ok".</summary>
  private static string SummarizeToolResult(ToolResult toolResult)
  {
    if (!toolResult.IsError)
    {
      return "ok";
    }

    string content = toolResult.Content;
    return content.Length > 80 ? content[..77] + "…" : content;
  }

  /// <summary>Composes the stream-fragment callback: the stamp fires first (liveness),
  ///     then the turn callback if present. Neither present means null — the provider
  ///     receives no callback at all, byte-identical to legacy.</summary>
  private Action<string>? WrapStreamStamp(Action<string>? turnCallback, string kind)
  {
    return (turnCallback, _onStreamActivity) switch
    {
      ({ } forward, { } stamp) => text =>
      {
        stamp(kind);
        forward(text);
      }
      ,
      ({ } onlyForward, _) => onlyForward,
      (_, { } onlyStamp) => text => onlyStamp(kind),
      _ => null,
    };
  }
  /// <summary>Drains every queued steering message into the conversation as User messages,
  /// preserving queue order. Safe points only: entry and iteration boundaries.</summary>
  private void DrainInbox(IAgentInbox? inbox)
  {
    if (inbox is null)
    {
      return;
    }

    int drained = 0;
    while (inbox.TryTake(out string? steered))
    {
      Conversation.AddUserMessage(steered);
      drained++;
    }

    if (drained > 0)
    {
      // W4.4: the push signal a host's unread badge clears on — mailbox lifecycle,
      // never run progress (the supervisor feed's pinned no-beat class).
      _events?.Publish(new MailboxDrainedEvent(Id, DateTimeOffset.UtcNow, drained));
    }
  }

  /// <summary>Closes the protocol gap an interruption can open: the trailing assistant
  /// message may hold tool calls whose results were never appended. Each unanswered call
  /// gets <see cref="InterruptedToolResult"/> so no later request carries dangling calls.</summary>
  private void RepairInterruptedToolCalls()
  {
    IReadOnlyList<Message> messages = Conversation.Messages;
    for (int i = messages.Count - 1; i >= 0; i--)
    {
      Message message = messages[i];
      if (message.Role is not Role.Assistant || message.ToolCalls is not { Count: > 0 } calls)
      {
        continue;
      }

      HashSet<string?> answered = [.. messages.Skip(i + 1)
          .Where(m => m.Role is Role.Tool && m.ToolCallId is not null)
          .Select(m => m.ToolCallId)];
      foreach (ToolCall? call in calls.Where(c => !answered.Contains(c.Id)))
      {
        Conversation.AddToolResult(call.Id, InterruptedToolResult);
      }

      return; // only the trailing batch can be incomplete
    }
  }
}
