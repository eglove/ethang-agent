using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>View-model for one open agent tab: owns that agent's transcript,
///     status bar, clarify presentation, and turn loop — the chat interaction
///     surface moved here from MainViewModel when the shell gained tabs. Several
///     instances coexist, one per open tab, so nothing here may be static or
///     process-global.</summary>
public sealed partial class AgentSessionViewModel : ObservableObject
{
    private readonly TurnRunner _runner;
    private readonly RootSessionLifecycle _lifecycle;
    private readonly AgentId _rootId;
    private readonly Conversation _conversation;
    private readonly Func<ClarifyQuestion, Task<ClarifyViewModel>> _presentClarify;
    private readonly Func<UiStreamEvent, Task> _streamSink;

    private IClarifyChannel? _clarifyChannel;
    private Task? _runningTurn;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _messageCount;

    [ObservableProperty]
    private string _input = "";

    public TranscriptViewModel Transcript { get; } = new();
    public StatusViewModel Status { get; }

    /// <summary>The workspace directory this agent works from (tab subtitle).</summary>
    public string WorkspaceRoot { get; }

    /// <summary>Tab caption: the workspace directory's name.</summary>
    public string Title => Path.GetFileName(WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>The pending clarify question, or null when none is awaiting an answer.</summary>
    [ObservableProperty]
    private ClarifyViewModel? _clarify;

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
        string modelId,
        string workspaceRoot,
        Func<ClarifyQuestion, Task<ClarifyViewModel>>? presentClarify = null,
        Func<UiStreamEvent, Task>? uiStreamSink = null,
        IAgentInbox? inbox = null,
        IAgentRuntime? childRuntime = null)
    {
        WorkspaceRoot = workspaceRoot;
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
        _streamSink = uiStreamSink ?? (evt => { ApplyStreamEvent(evt); return Task.CompletedTask; });
        Status = new StatusViewModel(modelId);
        _inbox = inbox;
        _childRuntime = childRuntime;
    }
    /// <summary>Stores the clarify channel seam the clarify tool answers through.</summary>
    public void AttachClarifyChannel(IClarifyChannel channel) => _clarifyChannel = channel;

    /// <summary>
    /// Presents a clarify question by building (and surfacing) its view-model through the
    /// injected present hook, publishing it as <see cref="Clarify"/>. Returns the view-model
    /// whose one-shot completion the channel awaits.
    /// </summary>
    public async Task<ClarifyViewModel> PresentClarifyAsync(ClarifyQuestion question)
    {
        var vm = await _presentClarify(question);
        // The panel must close whenever the question settles through ANY path —
        // routed input, option buttons, free-text submit, or cancel — not just the
        // routed-input path that clears it below. Settlement raises the view-model's
        // Settled event synchronously, so the close is deterministic and needs no
        // polling, timers, or fire-and-forget tasks.
        vm.Settled += (_, _) =>
        {
            if (ReferenceEquals(Clarify, vm))
                Clarify = null;
        };
        Clarify = vm;
        // A presenter may hand back an already-settled view-model (its answer was
        // known before presentation): the event has already fired, so close now.
        if (ReferenceEquals(Clarify, vm) && vm.Completion.IsCompleted)
            Clarify = null;
        return vm;
    }

    /// <summary>
    /// Processes one submission. Returns immediately for blank/command/busy inputs.
    /// For normal turns, sets the in-flight task and returns that same task so callers
    /// can await it directly.
    /// </summary>
    public Task SubmitAsync(string rawInput)
    {
        var input = rawInput?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return Task.CompletedTask;

        // Stop outranks everything: it must reach a busy turn even while a clarify question
        // from that same turn is pending.
        if (DesktopCommands.IsStop(input))
        {
            RequestStop();
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

        if (DesktopCommands.IsQuit(input))
        {
            // In the shell world there is no single window to close: quitting an agent
            // surfaces as closing its tab, which the view wires to CloseRequested.
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        if (DesktopCommands.IsHelp(input))
        {
            Transcript.AddNotice("Commands:" + string.Join("",
                DesktopCommands.All
                    .OrderBy(c => c.Name, StringComparer.Ordinal)
                    .Select(c => $"\n  {c.Name}  —  {c.Description}")));
            return Task.CompletedTask;
        }

        // Real turn — start and track it.
        _runningTurn = ExecuteTurnAsync(input);
        return _runningTurn;
    }

    /// <summary>Raised when the agent asks to close (e.g. /exit). The view closes the tab.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Awaits the in-flight turn task. Returns a completed task if no turn is running.</summary>
    public Task WaitForTurnAsync() => _runningTurn ?? Task.CompletedTask;

    /// <summary>
    /// Completes the root session on graceful exit (/exit, /quit, or tab close).
    /// Persistence failures surface as transcript notices — teardown itself never throws.
    /// </summary>
    public Task ShutdownAsync() => _lifecycle.CompleteAsync(_rootId, ReportPersistenceError);

    private async Task ExecuteTurnAsync(string input)
    {
        MessageCount++;
        Transcript.AddUser(input);
        Status.Phase = TurnPhase.Thinking;
        IsBusy = true;

        var cts = new CancellationTokenSource();
        _turnCts = cts;

        var bridge = new StreamBridge(_streamSink);
        bridge.Start();

        var messageCountBefore = _conversation.Messages.Count;
        var sawStream = false;
        Result<string> result;

        try
        {
            result = await _runner(
                new SendMessageCommand(input),
                cts.Token,
                onContentDelta: d => { sawStream = true; bridge.OnContentDelta(d); },
                onReasoningDelta: bridge.OnReasoningDelta,
                onIterationEnd: bridge.OnIterationEnd,
                onToolCall: bridge.OnToolCall,
                onToolResult: bridge.OnToolResult);

            // Close the channel so the pump can drain all buffered events.
            bridge.MarkTurnComplete();

            try
            {
                await bridge.DrainUntilIdleAsync();
            }
            catch (Exception ex)
            {
                // Sink fault — surface as an error notice rather than crashing the VM.
                Transcript.AddNotice($"Error [StreamFault]: {ex.Message}");
                return;
            }

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
        var cts = _turnCts;
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
        else if (TryParseLeadingInteger(input, out var choice))
        {
            pending.ChooseOption(choice);
        }
        else
        {
            pending.RejectInput($"Enter a number between 1 and {pending.Options.Count}.");
            return;
        }

        if (!pending.Completion.IsCompleted) return; // transient failure — stay pending

        Transcript.AddUser(input);
        Clarify = null;
    }

    private static bool TryParseLeadingInteger(string input, out int value)
    {
        var end = 0;
        while (end < input.Length && char.IsDigit(input[end])) end++;
        return int.TryParse(input.AsSpan(0, end), out value);
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
