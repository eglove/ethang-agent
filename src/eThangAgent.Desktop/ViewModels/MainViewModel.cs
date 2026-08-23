using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.ViewModels;

public delegate Task<Result<string>> TurnRunner(
    SendMessageCommand command,
    CancellationToken ct,
    Action<string>? onContentDelta,
    Action<string>? onReasoningDelta,
    Action? onIterationEnd,
    Action<string, string>? onToolCall,
    Action<string, string>? onToolResult);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly TurnRunner _runner;
    private readonly RootSessionLifecycle _lifecycle;
    private readonly AgentId _rootId;
    private readonly Conversation _conversation;
    private readonly Action _requestClose;
    private readonly Func<ClarifyQuestion, Task<ClarifyViewModel>> _presentClarify;

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

    /// <summary>The pending clarify question, or null when none is awaiting an answer.</summary>
    [ObservableProperty]
    private ClarifyViewModel? _clarify;

    public ICommand SubmitCommand { get; }

    public MainViewModel(
        TurnRunner runner,
        RootSessionLifecycle lifecycle,
        AgentId rootId,
        Conversation conversation,
        string modelId,
        Action requestClose,
        Func<ClarifyQuestion, Task<ClarifyViewModel>>? presentClarify = null)
    {
        _runner = runner;
        _lifecycle = lifecycle;
        _rootId = rootId;
        _conversation = conversation;
        _requestClose = requestClose;
        // Default present builds the view-model inline. Production (Task 12) supplies a
        // hook that marshals construction onto the UI thread via the Dispatcher.
        _presentClarify = presentClarify ?? (q => Task.FromResult(new ClarifyViewModel(q)));
        Status = new StatusViewModel(modelId);
        SubmitCommand = new AsyncRelayCommand(
            () => SubmitAsync(Input),
            () => !IsBusy);
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
        Clarify = vm;
        return vm;
    }

    /// <summary>
    /// Processes one submission. Returns immediately for blank/command/busy inputs.
    /// For normal turns, sets <see cref="_runningTurn"/> to the in-flight task and
    /// returns that same task so callers can await it directly.
    /// </summary>
    public Task SubmitAsync(string rawInput)
    {
        var input = rawInput?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return Task.CompletedTask;

        // A pending clarify question intercepts input before anything else — even while a
        // turn is in flight (IsBusy) awaiting the answer.
        if (Clarify is { } pending)
        {
            RouteToClarify(pending, input);
            return Task.CompletedTask;
        }

        if (IsBusy) return Task.CompletedTask;

        if (DesktopCommands.IsQuit(input))
        {
            _requestClose();
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

    /// <summary>Awaits the in-flight turn task. Returns a completed task if no turn is running.</summary>
    public Task WaitForTurnAsync() => _runningTurn ?? Task.CompletedTask;

    private async Task ExecuteTurnAsync(string input)
    {
        MessageCount++;
        Transcript.AddUser(input);
        Status.Phase = TurnPhase.Thinking;
        IsBusy = true;

        var bridge = new StreamBridge(ApplyStreamEvent);
        bridge.Start();

        var messageCountBefore = _conversation.Messages.Count;
        var sawStream = false;
        Result<string> result;

        try
        {
            result = await _runner(
                new SendMessageCommand(input),
                CancellationToken.None,
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
            Status.Phase = TurnPhase.Ready;
            IsBusy = false;
        }
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
            pending.ValidationMessage = $"Enter a number between 1 and {pending.Options.Count}.";
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

    private void ReportPersistenceError(string message) => Transcript.AddNotice(message);
}
