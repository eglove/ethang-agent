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

  /// <summary>Appended as a System message after a length-truncated assistant response.
  ///     Verbatim contract: the model must resume exactly where it stopped.</summary>
  public const string ContinuationPrompt =
      "[Your previous message was cut off by the output limit. Continue exactly where you stopped; do not repeat earlier text.]";

  /// <summary>Error code returned (as a Result failure, never an exception) when the turn's
  ///     token fires mid-loop.</summary>
  public const string TurnCancelledCode = "TurnCancelled";

  /// <summary>Synthetic tool result appended for each tool call left unanswered by an
  ///     interruption. Verbatim contract: tells the model why its calls produced nothing.</summary>
  public const string InterruptedToolResult = "[turn interrupted by the user; this call never ran]";

  private readonly IModelProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));
  private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));
  private readonly ISystemPromptProvider? _systemPrompt = options?.SystemPrompt;
  private readonly IContextMonitor? _contextMonitor = options?.ContextMonitor;
  private readonly IContextCompactor? _contextCompactor = options?.ContextCompactor;
  private readonly double _compactionThreshold = options?.CompactionThreshold ?? DefaultCompactionThreshold;
  private readonly int _maxAutoContinuations = options?.MaxAutoContinuations ?? DefaultMaxAutoContinuations;

  public Conversation Conversation { get; } = conversation ?? throw new ArgumentNullException(nameof(conversation));
  public ModelConfig Config { get; } = config ?? throw new ArgumentNullException(nameof(config));

  /// <summary>Identity of this agent. Roots generate one on construction; spawned children carry their persisted id.</summary>
  public AgentId Id { get; } = options?.Id ?? AgentId.NewId();

  /// <summary>Depth in the spawn tree. Root agents are depth 0; children run at parent depth + 1.</summary>
  public int Depth { get; } = options?.Depth ?? 0;

  /// <summary>Tool calls executed during the most recent SendMessage; 0 when the turn ended without any.</summary>
  public int LastTurnToolCalls { get; private set; }

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
      CancellationToken ct = default)
  {
    try
    {
      LastTurnToolCalls = 0;
      // Auto-continuations used by this turn only: reset here, never carried between turns.
      int autoContinuations = 0;
      DrainInbox(inbox);
      Conversation.AddUserMessage(text);
      // No iteration cap by design: the loop runs until the model answers without
      // tool calls. Termination is the model's job — but cancellation is checked
      // every round, because nothing else in the loop is obliged to observe ct
      // (fakes, cached providers, and instant tools may never see it).
      bool compactionFailed = false;
      while (true)
      {
        ct.ThrowIfCancellationRequested();
        DrainInbox(inbox);
        if (_contextCompactor is not null && !compactionFailed && _contextMonitor is { } monitor)
        {
          double? utilization = monitor.Status.UtilizationPercent;
          if (utilization >= _compactionThreshold)
          {
            Result<CompactionOutcome> compacted =
                await _contextCompactor.CompactAsync(Conversation, Config, ct).ConfigureAwait(false);
            if (compacted.IsSuccess)
            {
              callbacks?.OnCompacted?.Invoke(compacted.Value);
              ReportUsageFromMonitor(callbacks);
            }
            else
            {
              // Graceful degradation: a System notice tells the model why nothing
              // changed; a turn-local flag keeps a broken compactor from spamming.
              compactionFailed = true;
              Conversation.AddSystemMessage(
                $"[Context compaction failed: {compacted.Error.Code} {compacted.Error.Message}; continuing without compaction.]");
            }
          }
        }

        // Snapshot, not view: Conversation.Messages is a live wrapper over the growing
        // list, so handing it out directly would let every consumer of this request
        // (retries, logging, tests) read messages added by later iterations.
        ModelRequest request = new(
            [.. Conversation.Messages], _tools.Definitions, _systemPrompt?.Build());
        Result<ModelResponse> result = await _provider.SendStreamingAsync(Config, request,
            callbacks?.OnContentDelta, callbacks?.OnReasoningDelta, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
          return Result.Failure<string>(result.Error);
        }

        callbacks?.OnIterationEnd?.Invoke();

        ModelResponse response = result.Value;
        ReportUsage(request, response, callbacks);
        if (response.ToolCalls.Count == 0)
        {
          string content = response.Content ?? "";

          // A length-truncated answer is not a final answer. Keep the partial
          // message in history, nudge the model to continue where it stopped,
          // and loop — bounded so a pathological model cannot spin forever.
          // (Named decision: auto-continuation is leniency with a visible cap,
          // never a silent retry.)
          if (response.FinishReason is FinishReason.Length)
          {
            if (autoContinuations >= _maxAutoContinuations)
            {
              return Result.Failure<string>(new DomainError("MaxOutputContinuations",
                  $"Output limit reached {_maxAutoContinuations + 1} times without a complete answer."));
            }

            autoContinuations++;
            Conversation.AddAssistantMessage(content);
            Conversation.AddSystemMessage(ContinuationPrompt);
            continue;
          }

          Conversation.AddAssistantMessage(content);
          return Result.Success(content);
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
    }

    long toolChars = request.Tools is null
        ? 0
        : request.Tools.Sum(t => t.Name.Length + t.Description.Length
            + t.Parameters.Sum(p => p.Name.Length + p.Description.Length)
            + t.RequiredParameters.Sum(rp => rp.Length));
    _contextMonitor.OnRequestUsage(usage,
        new ContextComposition(systemPromptChars, messageChars, toolChars));
    callbacks?.OnContextUpdate?.Invoke(new ContextSnapshot(_contextMonitor.Status, _contextMonitor.Breakdown));
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
      ITool? tool = _tools.Find(call.Name);
      ToolResult toolResult = tool is null
          ? new ToolResult($"Error [UnknownTool]: Unknown tool: {call.Name}.", true)
          : await tool.ExecuteAsync(new RawToolInput(call.Name, call.Arguments), ct).ConfigureAwait(false);
      Conversation.AddToolResult(call.Id, toolResult.Content);
      string summary = SummarizeToolResult(toolResult);
      callbacks?.OnToolResult?.Invoke(call.Name, summary);
    }
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

    while (inbox.TryTake(out string? steered))
    {
      Conversation.AddUserMessage(steered);
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
