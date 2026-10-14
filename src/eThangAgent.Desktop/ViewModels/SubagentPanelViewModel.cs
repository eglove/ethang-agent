using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One row of the subagent split view's accordion: a spawned child agent the
///     session is running (or has run this session). Rows arrive by SUBSCRIPTION to the
///     session's child-event stream (started adds, settled marks done) — never polling
///     the runtime. The realtime transcript is polled from the store (the only
///     transcript seam) on a bounded refresh, rendered through the same
///     TranscriptViewModel the main chat uses. Events arrive on stream threads; the
///     caller supplies the marshal wrapper (the shell posts through the Dispatcher),
///     so every state mutation lands on the UI thread.</summary>
internal sealed partial class SubagentRowViewModel(
    AgentId id,
    string label,
    IAgentStore? store) : ObservableObject, IDisposable
{
  private readonly IAgentStore? _store = store;
  private int _lastRenderedCount;
  private bool _terminalFailed;

  public AgentId Id { get; } = id;

  public string Label { get; } = label;

  /// <summary>Rendered realtime transcript for this child — the store's incremental
  ///     persistence replayed as it lands.</summary>
  public TranscriptViewModel Transcript { get; } = new();

  [ObservableProperty]
  public partial bool IsRunning { get; set; } = true;

  [ObservableProperty]
  public partial string PhaseLabel { get; set; } = "starting";

  [ObservableProperty]
  public partial bool IsExpanded { get; set; }

  /// <summary>The row's status chip: the phase while running, the terminal state after.</summary>
  public string StatusDisplay => IsRunning ? PhaseLabel : TerminalDisplay;

  private string TerminalDisplay { get; set; } = "done";

  /// <summary>Chip color: running amber, done green, failed red.</summary>
  public Avalonia.Media.IBrush StatusBrush
  {
    get
    {
      Avalonia.Media.IBrush terminalBrush = _terminalFailed
          ? Avalonia.Media.Brushes.IndianRed
          : Avalonia.Media.Brushes.SeaGreen;
      return IsRunning ? Avalonia.Media.Brushes.Orange : terminalBrush;
    }
  }

  /// <summary>Applies a progress event's phase label (UI thread by contract).</summary>
  public void ApplyProgress(string label)
  {
    PhaseLabel = label;
    OnPropertyChanged(nameof(StatusDisplay));
  }

  /// <summary>Marks the run settled (UI thread by contract).</summary>
  public void ApplySettled(AgentStatus status, AgentFailureReason? reason)
  {
    IsRunning = false;
    _terminalFailed = status == AgentStatus.Failed;
    TerminalDisplay = status switch
    {
      AgentStatus.Running => "running",
      AgentStatus.Completed => "done",
      AgentStatus.Failed => "failed: " + (reason?.ToString() ?? "unknown"),
      AgentStatus.Interrupted => "interrupted",
      _ => status.ToString(),
    };
    OnPropertyChanged(nameof(StatusDisplay));
    OnPropertyChanged(nameof(StatusBrush));
  }

  /// <summary>Refreshes the transcript from the store (UI thread by contract): appends
  ///     only what landed since the last refresh, so a growing transcript never
  ///     re-renders wholesale. A store miss keeps the last state — the next refresh
  ///     retries. The FIRST refresh replays everything (initial attach).</summary>
  public async Task RefreshTranscriptAsync()
  {
    if (_store is null || !IsRunning)
    {
      return;
    }

    Result<IReadOnlyList<Message>> persisted = await _store.GetTranscriptAsync(Id).ConfigureAwait(true);
    if (!persisted.IsSuccess)
    {
      return;
    }

    IReadOnlyList<Message> messages = persisted.Value;
    for (int i = _lastRenderedCount; i < messages.Count; i++)
    {
      AppendMessage(messages[i]);
    }

    _lastRenderedCount = messages.Count;
  }

  /// <summary>Appends one persisted message to the rendered transcript. Mirrors
  ///     TranscriptViewModel.Restore's per-role mapping, minus images (child
  ///     transcripts carry no image parts in practice; a part that appears renders
  ///     text-only rather than crashing the panel).</summary>
  private void AppendMessage(Message message)
  {
    switch (message.Role)
    {
      case Role.User:
        Transcript.AddUser(message.Content);
        break;
      case Role.Assistant:
        if (message.Content.Length > 0)
        {
          Transcript.AddNotice(message.Content);
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
          foreach (ToolCall call in calls)
          {
            Transcript.AddToolCall(call.Name, call.Arguments);
          }
        }

        break;
      case Role.Tool:
        Transcript.AddToolResult("tool", "ok", message.Content, false);
        break;
      case Role.System:
        Transcript.AddSystemMessage(message.Content);
        break;
      default:
        break;
    }
  }

  public void Dispose() => Transcript.DisposeEntries();
}

/// <summary>The split view's right-hand panel: the session's running subagents in an
///     accordion. Attach subscribes ONE subscriber to the session's child-event
///     stream; rows add on ChildStartedEvent, phase labels update on progress, rows
///     mark terminal on ChildSettledEvent. A bounded refresh timer (the view's
///     existing 80 ms timer cadence is too hot for store reads; 500 ms) pulls
///     transcripts for RUNNING rows only. Panel visibility follows row presence:
///     no rows ever = collapsed (the main chat owns the full width). Null stream
///     (headless hosts) attaches nothing. Threading: events arrive on stream
///     threads — the marshal wrapper posts state mutations onto the UI thread.</summary>
internal sealed partial class SubagentPanelViewModel(IAgentStore? store) : ObservableObject, IDisposable
{
  private readonly IAgentStore? _store = store;
  private readonly Dictionary<AgentId, SubagentRowViewModel> _rowsById = [];
  private IDisposable? _lease;

  public ObservableCollection<SubagentRowViewModel> Rows { get; } = [];

  /// <summary>Whether the split view's right column shows at all: any row exists.</summary>
  public bool HasRows => Rows.Count > 0;

  [ObservableProperty]
  public partial SubagentRowViewModel? SelectedRow { get; set; }

  /// <summary>Subscribes the panel to a session's child-event stream. Null stream:
  ///     attaches nothing (headless hosts). Calling again detaches the prior lease.</summary>
  public void Attach(IAgentEvents? events)
  {
    _lease?.Dispose();
    _lease = null;
    if (events is null)
    {
      return;
    }

    SubagentEventSubscriber subscriber = new(this);
    _lease = events.Subscribe(subscriber);
  }

  /// <summary>Refreshes every RUNNING row's transcript from the store. Called by the
  ///     view's timer; marshals nothing itself — runs on the UI thread by contract.</summary>
  public async Task RefreshRunningTranscriptsAsync()
  {
    foreach (SubagentRowViewModel row in Rows)
    {
      if (row.IsRunning)
      {
        await row.RefreshTranscriptAsync().ConfigureAwait(true);
      }
    }
  }

  /// <summary>Event-stream adapter: routes events to the panel through the marshal
  ///     wrapper so mutations land on the UI thread. Contained: a fault in panel
  ///     handling never propagates into the child loop.</summary>
  private sealed class SubagentEventSubscriber(SubagentPanelViewModel panel) : IAgentEventSubscriber
  {
    public void OnEvent(ChildEvent evt)
    {
      // Named decision (CA1031): presentation containment — the stream's other
      // subscribers must never see a UI fault.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        panel.OnEvent(evt);
      }
      catch
      {
        // Swallowed deliberately: a UI hiccup must not take down the stream.
      }
#pragma warning restore CA1031
    }
  }

  /// <summary>Applies one child event (marshal-wrapped onto the UI thread by the shell).</summary>
  internal void OnEvent(ChildEvent evt)
  {
    switch (evt)
    {
      case ChildStartedEvent started:
        AddRow(started);
        break;
      case ChildProgressEvent progress:
        if (_rowsById.TryGetValue(progress.ChildId, out SubagentRowViewModel? row))
        {
          row.ApplyProgress(progress.Label);
        }

        break;
      case ChildSettledEvent settled:
        if (_rowsById.TryGetValue(settled.ChildId, out SubagentRowViewModel? settledRow))
        {
          settledRow.ApplySettled(settled.TerminalStatus, settled.Reason);
        }

        break;
      default:
        break; // mailbox, budget, idle, preemption: not accordion facts
    }
  }

  private void AddRow(ChildStartedEvent started)
  {
    if (_rowsById.ContainsKey(started.ChildId))
    {
      return; // watchdog same-id retry: the row already exists
    }

    SubagentRowViewModel row = new(started.ChildId, LabelFor(started), _store);
    _rowsById[started.ChildId] = row;
    Rows.Add(row);
    OnPropertyChanged(nameof(HasRows));
    // The newest row expands; the previous selection stays where it is.
    SelectedRow = row;
    row.IsExpanded = true;
  }

  private static string LabelFor(ChildStartedEvent started)
  {
    // D5: events carry metadata only — the label is the short id plus the model;
    // the task prompt lives in the child's transcript (rendered below).
    string shortId = started.ChildId.Value.ToString("N")[..8];
    return $"{shortId} · {started.ModelId}";
  }

  public void Dispose()
  {
    _lease?.Dispose();
    foreach (SubagentRowViewModel row in _rowsById.Values)
    {
      row.Dispose();
    }
  }
}
