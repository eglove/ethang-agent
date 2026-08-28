using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>View-model for one open agent tab: owns that agent's transcript,
///     status bar, clarify presentation, and turn loop — the chat interaction
///     surface moved here from MainViewModel when the shell gained tabs. Several
///     instances coexist, one per open tab, so nothing here may be static or
///     process-global.</summary>
internal sealed partial class AgentSessionViewModel : ObservableObject
{
  private readonly TurnRunner _runner;
  private readonly RootSessionLifecycle _lifecycle;
  private readonly AgentId _rootId;
  private readonly Conversation _conversation;
  private readonly Func<ClarifyQuestion, Task<ClarifyViewModel>> _presentClarify;
  private readonly Func<UiStreamEvent, Task> _streamSink;
  private readonly Action<string>? _statusModelUpdater;
  private readonly SessionModelPreferences? _modelPreferences;

  /// <summary>The session's bootstrap model (the provider default). Serves as the
  ///     status-bar display when the user returns the session to automatic choice.</summary>
  private readonly string _sessionDefaultModelId;

  private Task? _runningTurn;

  [ObservableProperty]
  public partial bool IsBusy { get; set; }

  [ObservableProperty]
  public partial int MessageCount { get; set; }

  [ObservableProperty]
  public partial string Input { get; set; } = "";

  public TranscriptViewModel Transcript { get; } = new();
  public StatusViewModel Status { get; }

  /// <summary>The workspace directory this agent works from (tab subtitle).</summary>
  public string WorkspaceRoot { get; }

  /// <summary>Tab caption: the workspace directory's name.</summary>
  public string Title => Path.GetFileName(WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

  /// <summary>The pending clarify question, or null when none is awaiting an answer.</summary>
  [ObservableProperty]
  public partial ClarifyViewModel? Clarify { get; set; }

  private readonly IAgentInbox? _inbox;
  private readonly IAgentRuntime? _childRuntime;

  /// <summary>The active turn's cancellation source, or null between turns. Assigned on the UI
  /// thread at turn start; cleared at turn end. Never disposed inline — cancelling a disposed
  /// source from the UI thread would race the worker's teardown.</summary>
  private CancellationTokenSource? _turnCts;

  public AgentSessionViewModel(TurnRunner runner,
      RootSessionLifecycle lifecycle,
      AgentId rootId,
      Conversation conversation,
      string provider,
      string modelId,
      string workspaceRoot,
      Func<ClarifyQuestion, Task<ClarifyViewModel>>? presentClarify = null,
      Func<UiStreamEvent, Task>? uiStreamSink = null,
      IAgentInbox? inbox = null,
      IAgentRuntime? childRuntime = null,
      Action<string>? statusModelUpdater = null,
      SessionModelPreferences? modelPreferences = null)
  {
    WorkspaceRoot = workspaceRoot;
    _statusModelUpdater = statusModelUpdater;
    // Turns must never run on the UI thread: their awaits would post back to
    // Avalonia's SynchronizationContext and one blocking tool call would freeze
    // the whole shell (see DesktopHost.OffUiThread). The schedule wraps EVERY
    // runner, stub or real, so the guarantee cannot be bypassed by callers.
    _runner = DesktopHost.OffUiThread(
        runner ?? throw new ArgumentNullException(nameof(runner)));
    _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    _rootId = rootId;
    _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
    // Default present builds the view-model inline. Production supplies hooks that
    // marshal onto the UI thread via the Dispatcher.
    _presentClarify = presentClarify ?? (q => Task.FromResult(new ClarifyViewModel(q)));
    // Default sink applies on the calling thread — adequate for unit tests.
    // Production passes ApplyUiStreamEventOnUIThreadAsync so transcript mutations
    // always land on the UI thread.
    _streamSink = uiStreamSink ?? (evt =>
    {
      ApplyStreamEvent(evt);
      return Task.CompletedTask;
    });
    Status = new StatusViewModel(provider, modelId);
    _modelPreferences = modelPreferences;
    _sessionDefaultModelId = modelId;
    _inbox = inbox;
    _childRuntime = childRuntime;
  }

  /// <summary>
  /// Presents a clarify question by building (and surfacing) its view-model through the
  /// injected present hook, publishing it as <see cref="Clarify"/>. Returns the view-model
  /// whose one-shot completion the channel awaits.
  /// </summary>
  public async Task<ClarifyViewModel> PresentClarifyAsync(ClarifyQuestion question)
  {
    ClarifyViewModel vm = await _presentClarify(question);
    // The panel must close whenever the question settles through ANY path —
    // routed input, option buttons, free-text submit, or cancel — not just the
    // routed-input path that clears it below. Settlement raises the view-model's
    // Settled event synchronously, so the close is deterministic and needs no
    // polling, timers, or fire-and-forget tasks.
    vm.Settled += (_, _) =>
    {
      if (ReferenceEquals(Clarify, vm))
      {
        Clarify = null;
      }
    };
    Clarify = vm;
    // A presenter may hand back an already-settled view-model (its answer was
    // known before presentation): the event has already fired, so close now.
    if (ReferenceEquals(Clarify, vm) && vm.Completion.IsCompleted)
    {
      Clarify = null;
    }

    return vm;
  }

  /// <summary>
  /// Processes one submission. Blank input is ignored. While a turn runs, input
  /// steers it. For normal turns, sets the in-flight task and returns that same
  /// task so callers can await it directly.
  /// </summary>
  public Task SubmitAsync(string rawInput)
  {
    string input = rawInput?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(input))
    {
      return Task.CompletedTask;
    }

    // A pending clarify question intercepts input before anything else — even while a
    // turn is in flight (IsBusy) awaiting the answer.
    if (Clarify is { } pending)
    {
      RouteToClarify(pending, input);
      return Task.CompletedTask;
    }

    // While a turn runs, input steers it: posted to the session inbox for delivery at the
    // loop's next safe point, and echoed into the transcript immediately. Never dropped.
    if (IsBusy)
    {
      Steer(input);
      return Task.CompletedTask;
    }

    // Real turn — start and track it.
    _runningTurn = ExecuteTurnAsync(input);
    return _runningTurn;
  }

  /// <summary>
  /// Applies the user's effort-picker choice: sets the session's reasoning effort
  /// (null returns it to the provider default). Applies from the next turn, root and
  /// children alike; persisted per workspace by the shell.
  /// </summary>
  public void ApplyEffortChoice(ReasoningEffort? effort)
  {
    if (_modelPreferences is null)
    {
      Transcript.AddNotice("Effort choice is unavailable in this session (no model preferences wired).");
      return;
    }

    _modelPreferences.ReasoningEffort = effort;
    Transcript.AddNotice(effort is null
        ? "Reasoning effort set to the model default; applies from the next turn."
        : $"Reasoning effort set to {EffortLevels.DisplayName(effort.Value)}; applies from the next turn.");
  }

  /// <summary>
  /// Applies the user's model picker choice: pins the session's model (null returns it
  /// to automatic choice — intelligent selection on OpenRouter, the provider default on
  /// z.ai), updates the status bar, and announces the change. Applies from the next turn,
  /// root and children alike; persisted per workspace by the shell.
  /// </summary>
  public void ApplyModelChoice(string? modelId)
  {
    if (_modelPreferences is null)
    {
      Transcript.AddNotice("Model choice is unavailable in this session (no model preferences wired).");
      return;
    }

    _modelPreferences.ModelId = modelId;
    Status.ModelId = modelId ?? _sessionDefaultModelId;
    Transcript.AddNotice(modelId is null
        ? "Model set to automatic choice; applies from the next turn."
        : $"Model set to {modelId}; applies from the next turn.");
  }

  /// <summary>Awaits the in-flight turn task. Returns a completed task if no turn is running.</summary>
  public Task WaitForTurnAsync() => _runningTurn ?? Task.CompletedTask;

  /// <summary>
  /// Completes the root session on graceful exit (tab close or window close).
  /// Persistence failures surface as transcript notices — teardown itself never throws.
  /// </summary>
  public Task ShutdownAsync() => _lifecycle.CompleteAsync(_rootId, ReportPersistenceError);

  private async Task ExecuteTurnAsync(string input)
  {
    MessageCount++;
    Transcript.AddUser(input);
    Status.Phase = TurnPhase.Thinking;
    IsBusy = true;

    CancellationTokenSource cts = new();
    _turnCts = cts;

    StreamBridge bridge = new(_streamSink);
    bridge.Start();

    int messageCountBefore = _conversation.Messages.Count;
    bool sawStream = false;
    Result<string> result;

    try
    {
      result = await _runner(
          new SendMessageCommand(input),
          cts.Token,
          onContentDelta: d =>
          {
            sawStream = true;
            bridge.OnContentDelta(d);
          },
          onReasoningDelta: bridge.OnReasoningDelta,
          onIterationEnd: bridge.OnIterationEnd,
          onToolCall: bridge.OnToolCall,
          onToolResult: bridge.OnToolResult,
          onNotice: notice =>
          {
            // Selection (and reselection) notices surface in the transcript so the user
            // sees which model was chosen and when selection fell back. The notice text
            // carries the resolved model id; refresh the status bar to match.
            Transcript.AddNotice(notice);
            if (_statusModelUpdater is not null)
            {
              string? resolved = TryExtractModelId(notice);
              if (resolved is not null)
              {
                _statusModelUpdater(resolved);
              }
            }
          });

      // Close the channel so the pump can drain all buffered events.
      bridge.MarkTurnComplete();

      try
      {
        await bridge.DrainUntilIdleAsync();
      }
      // Named decision (CA1031): a sink fault must surface as an error notice —
      // the turn loop keeps running and the VM never crashes.
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex)
      {
        // Sink fault — surface as an error notice rather than crashing the VM.
        Transcript.AddNotice($"Error [StreamFault]: {ex.Message}");
        return;
      }
#pragma warning restore CA1031

      await _lifecycle.AppendExchangeAsync(
          _rootId, _conversation, messageCountBefore, result, ReportPersistenceError);

      // Non-streaming fallback: if no deltas were delivered, show the final text as a notice.
      if (!result.IsSuccess || !sawStream)
      {
        Transcript.AddNotice(result.IsSuccess
            ? result.Value!
            : $"Error [{result.Error!.Code}]: {result.Error.Message}");
      }
    }
    finally
    {
      _turnCts = null;
      Status.Phase = TurnPhase.Ready;
      IsBusy = false;
    }
  }

  /// <summary>
  /// Interrupts the running turn and all of this session's sub-agents. Hard cancel: the
  /// domain repairs any half-finished tool batch so history stays valid, and surfaces the
  /// interruption as a TurnCancelled result. No-op with a notice when idle. Safe to call
  /// repeatedly; only the first call per turn has effect.
  /// </summary>
  public void RequestStop()
  {
    CancellationTokenSource? cts = _turnCts;
    if (!IsBusy || cts is null)
    {
      Transcript.AddNotice("No active turn to stop.");
      return;
    }
    _childRuntime?.Interrupt();
    cts.Cancel();
  }

  /// <summary>Posts input to the session inbox as steering for the running turn and echoes
  /// it into the transcript. The model sees it on the provider call after the current tool
  /// batch completes; if the turn ends first, it opens the next one.</summary>
  private void Steer(string input)
  {
    if (_inbox is null)
    {
      Transcript.AddNotice("Error [NoInbox]: This session cannot accept steering messages.");
      return;
    }
    _inbox.Post(input);
    MessageCount++;
    Transcript.AddUser(input);
  }

  private void ApplyStreamEvent(UiStreamEvent evt)
  {
    switch (evt)
    {
      case UiStreamEvent.Delta d:
        Status.Phase = TurnPhase.Streaming;
        Transcript.AppendAssistantDelta(d.Text);
        break;
      case UiStreamEvent.Reasoning r:
        Transcript.AppendReasoning(r.Text);
        break;
      case UiStreamEvent.IterationEnd:
        Transcript.EndIteration();
        break;
      case UiStreamEvent.ToolCallEvent tc:
        Transcript.AddToolCall(tc.Name, tc.Arguments);
        break;
      case UiStreamEvent.ToolResultEvent tr:
        Transcript.AddToolResult(tr.Name, tr.Summary);
        break;
      default:
        break;
    }
  }

  /// <summary>
  /// Answers the pending clarify question. Free-text questions submit the trimmed input;
  /// numbered questions parse a leading integer and select that option. A settled question
  /// records the answer as a user transcript entry and clears <see cref="Clarify"/> — it
  /// never increments <see cref="MessageCount"/> nor starts a turn. Invalid input leaves
  /// the question pending with its validation message showing.
  /// </summary>
  private void RouteToClarify(ClarifyViewModel pending, string input)
  {
    if (pending.AllowFreeText)
    {
      pending.Input = input;
      pending.SubmitFreeText();
    }
    else if (TryParseLeadingInteger(input, out int choice))
    {
      pending.ChooseOption(choice);
    }
    else
    {
      pending.RejectInput($"Enter a number between 1 and {pending.Options.Count}.");
      return;
    }

    if (!pending.Completion.IsCompleted)
    {
      return; // transient failure — stay pending
    }

    Transcript.AddUser(input);
    Clarify = null;
  }

  private static bool TryParseLeadingInteger(string input, out int value)
  {
    int end = 0;
    while (end < input.Length && char.IsDigit(input[end]))
    {
      end++;
    }

    return int.TryParse(input.AsSpan(0, end), out value);
  }

  /// <summary>Parses the resolved model id from a RootAgentResolver notice so the status
  ///     bar can track mid-session reselection. Recognizes both verbatim notice contracts:
  ///     "Model selected: &lt;id&gt;" (success) and "... using &lt;id&gt;." (fallback).
  ///     Returns null when the notice carries no recognizable model id.</summary>
  private static string? TryExtractModelId(string notice)
  {
    const string selectedPrefix = "Model selected: ";
    int sel = notice.IndexOf(selectedPrefix, StringComparison.Ordinal);
    if (sel >= 0)
    {
      return notice[(sel + selectedPrefix.Length)..].Trim();
    }

    const string usingPrefix = "using ";
    int use = notice.IndexOf(usingPrefix, StringComparison.Ordinal);
    if (use >= 0)
    {
      string rest = notice[(use + usingPrefix.Length)..];
      int dot = rest.IndexOf('.', StringComparison.Ordinal);
      if (dot >= 0)
      {
        rest = rest[..dot];
      }

      return rest.Trim();
    }

    return null;
  }

  /// <summary>Applies a stream event on the calling thread. Test seam; also usable when
  ///     the caller has already marshaled onto the UI thread.</summary>
  public Task ApplyUiStreamEventAsync(UiStreamEvent evt)
  {
    ApplyStreamEvent(evt);
    return Task.CompletedTask;
  }

  /// <summary>Production stream sink: marshals the event onto the UI thread before applying
  ///     it, keeping every <see cref="Transcript"/> mutation on the UI thread.</summary>
  public Task ApplyUiStreamEventOnUIThreadAsync(UiStreamEvent evt) =>
      Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyStreamEvent(evt)).GetTask();

  private void ReportPersistenceError(string message) => Transcript.AddNotice(message);
}
