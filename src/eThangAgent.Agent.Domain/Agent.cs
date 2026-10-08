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

  /// <summary>Default utilization percent that trips the compactor.</summary>
  public const double DefaultCompactionThreshold = 80.0;


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

  /// <summary>Synthetic tool result appended for each tool call left unanswered by an
  ///     interruption. Verbatim contract: tells the model why its calls produced nothing.</summary>
  public const string InterruptedToolResult = "[turn interrupted by the user; this call never ran]";

  /// <summary>Verbatim prefix of the System message appended when the provider fails
  ///     mid-turn: the persisted transcript records WHY the turn stopped instead of
  ///     ending on a bare tool result (the failure was previously UI-notice only).</summary>
  public const string TurnFailedPrefix = "[turn failed] ";

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
  private readonly double _compactionThreshold = options?.CompactionThreshold ?? DefaultCompactionThreshold;
  private readonly int _maxAutoContinuations = options?.MaxAutoContinuations ?? DefaultMaxAutoContinuations;
  private readonly string? _sessionId = options?.SessionId;
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
  ///     Observable for hosts and tests: non-null means the loop persists incrementally.</summary>
  internal IDisposable? TranscriptSubscription { get; } = options?.TranscriptSink is { } transcriptSink
      ? SubscribeSink(conversation, transcriptSink)
      : null;

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
      LastTurnToolCalls = 0;
      ShrankThisTurn = false;
      _shrankThisTurnObserved = false;
      _repeatGuard.Reset();
      _readLedger.Reset();
      // Auto-continuations used by this turn only: reset here, never carried between turns.
      int autoContinuations = 0;
      // Overflow recovery is turn-local (like autoContinuations): one forced compaction
      // and one re-send per turn — a second overflow fails the turn, bounded.
      bool overflowRecovered = false;
      DrainInbox(inbox);
      Conversation.AddUserMessage(text, imageParts);
      Beat();
      // No iteration cap by design: the loop runs until the model answers without
      // tool calls. Termination is the model's job — but cancellation is checked
      // every round, because nothing else in the loop is obliged to observe ct
      // (fakes, cached providers, and instant tools may never see it).
      bool autoCompactBlocked = false;
      while (true)
      {
        ct.ThrowIfCancellationRequested();
        Beat();
        DrainInbox(inbox);
        if (!autoCompactBlocked)
        {
          bool shrinkObserved = SetShrankIfObserved();
          if (shrinkObserved)
          {
            callbacks?.OnContextShrunk?.Invoke();
          }

          bool compacted = !shrinkObserved && await TryCompactIfNeededAsync(callbacks, ct).ConfigureAwait(false);
          autoCompactBlocked = compacted || shrinkObserved;
        }
        // Snapshot, not view: Conversation.Messages is a live wrapper over the growing
        // list, so handing it out directly would let every consumer of this request
        // (retries, logging, tests) read messages added by later iterations.
        ModelRequest request = new(
            [.. Conversation.Messages], _tools.Definitions, _systemPrompt?.Build(), _sessionId);
        Result<ModelResponse> result = await _provider.SendStreamingAsync(Config, request,
            callbacks?.OnContentDelta, callbacks?.OnReasoningDelta, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
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
          if (result.Error.Code is ContextWindowExceededCode && !overflowRecovered)
          {
            Result<string>? recovered = await RecoverFromContextOverflowAsync(
                result.Error, callbacks, ct).ConfigureAwait(false);
            if (recovered is null)
            {
              overflowRecovered = true;
              // The monitor's utilization report predates the overflow — re-running the
              // threshold check on it would compact again on stale state. The turn rests
              // the backstop here exactly as a successful threshold compaction does.
              autoCompactBlocked = true;
              continue;
            }

            return recovered;
          }

          // The failure becomes part of the transcript: a turn that dies on a provider
          // error must not leave the conversation ending on a bare tool result with no
          // record of why — the model (and any resumed session) reads this line.
          string failure = $"{TurnFailedPrefix}Error [{result.Error.Code}]: {result.Error.Message}";
          Conversation.AddSystemMessage(failure);
          callbacks?.OnSystemMessage?.Invoke(failure);
          return Result.Failure<string>(result.Error);
        }

        callbacks?.OnIterationEnd?.Invoke();

        ModelResponse response = result.Value;
        ReportUsage(request, response, callbacks);
        SurfaceServerToolCalls(response, callbacks);
        if (response.ToolCalls.Count == 0)
        {
          if (FinishWithoutToolCalls(response, ref autoContinuations, callbacks) is { } outcome)
          {
            return outcome;
          }

          continue;
        }

        Conversation.AddAssistantMessage(response.Content ?? "",
            [.. response.ToolCalls.Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))]);
        await ExecuteToolCallsAsync(response.ToolCalls, callbacks, ct).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException)
    {
      RepairInterruptedToolCalls();
      return Result.Failure<string>(new DomainError(TurnCancelledCode, RuntimeErrors.TurnCancelled));
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

  /// <summary>Compaction phase of an iteration: when a compactor and monitor are wired
  ///     and utilization sits at or above the threshold, runs the compactor. Success
  ///     fires <see cref="TurnCallbacks.OnCompacted"/> and re-fires the context snapshot;
  ///     failure appends a System notice telling the model why nothing changed and
  ///     returns true so a broken compactor stops being retried this turn.</summary>
  private async Task<bool> TryCompactIfNeededAsync(TurnCallbacks? callbacks, CancellationToken ct)
  {
    if (_contextCompactor is null || _contextMonitor is not { } monitor)
    {
      return false;
    }

    // Null utilization (nothing reported yet) never compacts — lifted >= semantics:
    // the original guard read utilization >= threshold, under which null falls through.
    if (monitor.Status.UtilizationPercent is not { } utilization || utilization < _compactionThreshold)
    {
      return false;
    }

    // Inflation guard: a provider-reported input-token count far beyond what the
    // conversation itself could hold is server-tool accounting, not context. Running
    // the compactor on it fails spuriously (CompactionImpossible on a tiny
    // conversation); skipping quietly is correct — the next honest report compacts.
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
    if (monitor.Status.LastInputTokens is { } reported
        && reported > (long)(estimateTokens * MaxTrustedUsageOverEstimate))
    {
      return false;
    }

    Result<CompactionOutcome> compacted =
        await _contextCompactor.CompactAsync(Conversation, Config, ct).ConfigureAwait(false);
    if (compacted.IsSuccess)
    {
      _readLedger.Reset();  // history was replaced: nothing held before is held now
      callbacks?.OnCompacted?.Invoke(compacted.Value);
      // The summary message the compaction left in the conversation is surfaced
      // verbatim: nothing the agent receives stays hidden from the host surface
      // (live parity with the resume replay, which restores it as a system
      // entry). A compactor that reports success without replacing the prefix
      // leaves nothing to surface - and nothing was received, so none is fired.
      if (Conversation.Messages.FirstOrDefault(m => m.IsSummary) is { } summary)
      {
        callbacks?.OnSystemMessage?.Invoke(summary.Content);
      }

      ReportUsageFromMonitor(callbacks);
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
    Result<CompactionOutcome> compacted =
        await _contextCompactor.CompactAsync(Conversation, Config, ct).ConfigureAwait(false);
    if (!compacted.IsSuccess)
    {
      string line = $"{TurnFailedPrefix}Error [{compacted.Error.Code}]: {compacted.Error.Message} (underlying: Error [{overflowError.Code}]: {overflowError.Message})";
      Conversation.AddSystemMessage(line);
      callbacks?.OnSystemMessage?.Invoke(line);
      return Result.Failure<string>(compacted.Error);
    }

    // Same observer surface as the threshold backstop: outcome, the summary line the
    // compaction left in the conversation, and a re-fired context snapshot.
    _readLedger.Reset();  // history was replaced: nothing held before is held now
    callbacks?.OnCompacted?.Invoke(compacted.Value);
    if (Conversation.Messages.FirstOrDefault(m => m.IsSummary) is { } summary)
    {
      callbacks?.OnSystemMessage?.Invoke(summary.Content);
    }

    ReportUsageFromMonitor(callbacks);
    return null;
  }

  /// <summary>Final-response policy for an iteration whose answer carries no tool calls.
  ///     A length-truncated answer is not final: the partial message stays in history,
  ///     the model is nudged to continue (bounded — a pathological model cannot spin
  ///     forever; leniency with a visible cap, never a silent retry), and null is
  ///     returned so the loop continues. A complete answer is appended and returned.</summary>
  private Result<string>? FinishWithoutToolCalls(ModelResponse response, ref int autoContinuations,
      TurnCallbacks? callbacks)
  {
    string content = response.Content ?? "";
    if (response.FinishReason is not FinishReason.Length)
    {
      Conversation.AddAssistantMessage(content);
      return Result.Success(content);
    }

    if (autoContinuations >= _maxAutoContinuations)
    {
      return Result.Failure<string>(new DomainError("MaxOutputContinuations",
          $"Output limit reached {_maxAutoContinuations + 1} times without a complete answer."));
    }

    autoContinuations++;
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
    long messageChars = 0;
    foreach (Message message in request.Messages)
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

    long toolChars = request.Tools is null ? 0 : request.Tools.Sum(ToolDefinitionChars);
    _contextMonitor.OnRequestUsage(usage,
        new ContextComposition(systemPromptChars, messageChars, toolChars));
    callbacks?.OnContextUpdate?.Invoke(new ContextSnapshot(_contextMonitor.Status, _contextMonitor.Breakdown));
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
  ///     conversation and reporting the call and its summary to the observers.</summary>
  private async Task ExecuteToolCallsAsync(IReadOnlyList<ToolCallRequest> calls,
      TurnCallbacks? callbacks, CancellationToken ct)
  {
    for (int i = 0; i < calls.Count; i++)
    {
      ToolCallRequest call = calls[i];
      LastTurnToolCalls++;
      callbacks?.OnToolCall?.Invoke(call.Name, call.Arguments, i + 1, calls.Count);
      // Suspension enforcement: a tool the repeat guard suspended for this turn is
      // refused without executing — the model reads the refusal as its tool result
      // and must take a different approach. The refusal itself is not observed by
      // the guard (it cannot extend or reset the streak).
      if (_repeatGuard.IsSuspended(call.Name))
      {
        string refusal = $"Error [ToolSuspended]: Tool '{call.Name}' is suspended for the rest of this turn after repeated failures. Take a different approach.";
        Conversation.AddToolResult(call.Id, refusal);
        callbacks?.OnToolResult?.Invoke(call.Name, refusal, refusal, true, null);
        continue;
      }
      ITool? tool = _tools.Find(call.Name);
      if (tool is ILedgerBoundTool ledgerTool && !ledgerTool.HasLedger)
      {
        tool = ledgerTool.WithLedger(_readLedger);
      }

      PublishProgress(ChildPhase.ToolExec, "tool:" + call.Name);
      _heartbeat?.Beat(Id);
      ToolResult toolResult = tool is null
          // A registry that owns the grant policy explains the refusal in its own words
          // (R1.3: GrantViolation, distinguishable from typo); a plain registry renders
          // the standard UnknownTool line.
          ? new ToolResult((_tools as FilteredToolRegistry)?.ExplainsRefusal(call.Name)
              ?? $"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
          : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct).ConfigureAwait(false);
      Conversation.AddToolResult(call.Id, toolResult.Content, ToParts(toolResult.Images));
      if (toolResult.Content.Contains(ContextShrinkSentinel, StringComparison.Ordinal))
      {
        _shrankThisTurnObserved = true;
        _readLedger.Reset();  // history was replaced: nothing held before is held now
      }
      if (_repeatGuard.Observe(call.Name, call.Arguments, toolResult.IsError, toolResult.Content) is { } guardLine)
      {
        Conversation.AddSystemMessage(guardLine);
        callbacks?.OnSystemMessage?.Invoke(guardLine);
      }
      _heartbeat?.Beat(Id);
      PublishProgress(ChildPhase.Draining, "tool-result");
      string summary = SummarizeToolResult(toolResult);
      callbacks?.OnToolResult?.Invoke(call.Name, summary, toolResult.Content, toolResult.IsError,
          toolResult.Title is null ? null : toolResult);
    }
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
  ///     The lines also land in the conversation so a resumed session sees them.</summary>
  private void SurfaceServerToolCalls(ModelResponse response, TurnCallbacks? callbacks)
  {
    if (response.ServerToolCalls.Count == 0)
    {
      return;
    }

    foreach (ServerToolCall call in response.ServerToolCalls)
    {
      string line = RenderServerToolCall(call);
      Conversation.AddSystemMessage(line);
      callbacks?.OnSystemMessage?.Invoke(line);
    }
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
