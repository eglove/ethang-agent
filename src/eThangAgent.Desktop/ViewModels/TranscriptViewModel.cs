using System.Collections.ObjectModel;
using System.Diagnostics;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>
/// Holds rendered transcript entries and applies stream events with the same
/// semantics as the terminal DrainStream: deltas extend the open block; iteration end
/// (or any non-stream event) closes it so the next delta opens a fresh entry. Tool cards
/// time their tools here: AddToolCall stamps the running call card at zero,
/// TickToolElapsed advances it, AddToolResult freezes the total onto the result card.
/// All methods run on the UI thread — callers marshal (Task 9 bridge).
/// </summary>
internal sealed class TranscriptViewModel(Func<double>? secondsClock = null)
{
  // Seconds source for the running tool's elapsed timing; injectable so tests
  // drive it deterministically instead of sleeping.
  private readonly Func<double>? _injectedClock = secondsClock;

  // The one running tool's timing state (tools execute sequentially, one at a
  // time); null when no tool is running or a turn ended mid-tool.
  private (int Index, double StartSeconds, ToolElapsedHandle Elapsed)? _runningTool;

  // Index of the extendable AssistantTextEntry / ReasoningEntry, else -1.
  private int _openIndex = -1;

  // Normalizer feeding the open ReasoningEntry: providers emit reasoning as tiny
  // fragments full of hard wraps and blank-line floods that must not reach the UI raw.
  private StreamedTextNormalizer? _openReasoning;

  public ObservableCollection<TranscriptEntry> Entries { get; } = [];

  /// <summary>Sticky auto-scroll state for this transcript. Lives on the
  ///     view-model (not the view) so a rebuilt AgentView - the tab switch path -
  ///     inherits the sticky state and last reading offset.</summary>
  public TranscriptScrollController Scroll { get; } = new();

  public void AddUser(string text)
  {
    CloseOpen();
    Entries.Add(new UserMessageEntry(text));
  }

  public void AddToolCall(string name, string arguments)
  {
    CloseOpen();
    ToolElapsedHandle elapsed = new(ToolElapsed.Format(0));
    Entries.Add(new ToolCallEntry(name, arguments, elapsed));
    _runningTool = (Entries.Count - 1, SecondsNow(), elapsed);
  }

  public void AddToolResult(string name, string summary, string fullContent, bool isError)
  {
    CloseOpen();
    double elapsed = _runningTool is { } running ? SecondsNow() - running.StartSeconds : 0;
    _runningTool = null;
    Entries.Add(new ToolResultEntry(name, summary, fullContent, isError, ToolElapsed.Format(elapsed, isError)));
  }

  public void AddNotice(string text)
  {
    CloseOpen();
    Entries.Add(new NoticeEntry(text));
  }

  public void EndIteration() => CloseOpen();

  /// <summary>
  /// Replays a persisted transcript into the entries list — the resume surface, called
  /// once on a fresh transcript. User → user entry; plain assistant → assistant entry;
  /// an assistant tool-call message → its text (when non-empty) plus one call entry per
  /// call; a tool result → result entry with the tool name resolved from the preceding
  /// assistant batch by <see cref="Message.ToolCallId"/> ("tool" when unresolvable);
  /// system messages (nudges, continuation prompts) → notices. Reasoning traces and
  /// stream notices are never persisted, so a restored transcript shows content only.
  /// </summary>
  public void Restore(IReadOnlyList<Message> messages)
  {
    ArgumentNullException.ThrowIfNull(messages);
    Dictionary<string, string> callNames = [];

    foreach (Message message in messages)
    {
      switch (message.Role)
      {
        case Role.User:
          AddUser(message.Content);
          break;
        case Role.Assistant:
          RestoreAssistant(callNames, message);
          break;
        case Role.Tool:
          Entries.Add(RestoredToolResult(callNames, message));
          break;
        case Role.System:
          AddNotice(message.Content);
          break;
        default:
          break;
      }
    }

    CloseOpen();
    _runningTool = null;
  }

  /// <summary>Restores one assistant message: its text (when non-empty) followed by one
  ///     call entry per requested tool call, recording names so the following results
  ///     can resolve theirs.</summary>
  private void RestoreAssistant(Dictionary<string, string> callNames, Message message)
  {
    CloseOpen();
    if (message.Content.Length > 0)
    {
      Entries.Add(new AssistantTextEntry(message.Content, IsOpen: false));
    }

    if (message.ToolCalls is { Count: > 0 } calls)
    {
      foreach (ToolCall call in calls)
      {
        callNames[call.Id] = call.Name;
        Entries.Add(new ToolCallEntry(call.Name, call.Arguments));
      }
    }
  }

  /// <summary>Builds the entry for one restored tool result, named from the preceding
  ///     assistant batch by tool-call id ("tool" when unresolvable). Transcripts
  ///     persist no error flag, so restored results render as non-errors.</summary>
  private static ToolResultEntry RestoredToolResult(Dictionary<string, string> callNames, Message message)
  {
    string name = message.ToolCallId is not null && callNames.TryGetValue(message.ToolCallId, out string? resolved)
        ? resolved
        : "tool";
    return new ToolResultEntry(name, "ok", message.Content, IsError: false);
  }

  public void AppendAssistantDelta(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
    {
      return; // empty fragment — no information, no block change
    }

    if (_openIndex >= 0 && Entries[_openIndex] is AssistantTextEntry open)
    {
      // Replace notification drives re-render.
      Entries[_openIndex] = open with { Text = open.Text + text, IsOpen = true };
      return;
    }

    CloseOpen();
    Entries.Add(new AssistantTextEntry(text, IsOpen: true));
    _openIndex = Entries.Count - 1;
  }

  public void AppendReasoning(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
    {
      return; // empty fragment — no information, no block change
    }

    if (_openIndex >= 0 && Entries[_openIndex] is ReasoningEntry open && _openReasoning is not null)
    {
      _openReasoning.Append(text);
      Entries[_openIndex] = open with { Text = _openReasoning.Text, IsOpen = true };
      return;
    }

    CloseOpen();
    StreamedTextNormalizer normalizer = new();
    normalizer.Append(text);
    _openReasoning = normalizer;
    Entries.Add(new ReasoningEntry(normalizer.Text, IsOpen: true));
    _openIndex = Entries.Count - 1;
  }

  /// <summary>Abandons the running tool's timing at turn end: a call card left
  ///     running when the turn stops (user stop, error) keeps the zero stamp it
  ///     displayed while it ran, frozen against later ticks.</summary>
  public void EndTurn() => _runningTool = null;

  /// <summary>Advances the running tool card's elapsed display by mutating the
  ///     entry's elapsed handle (an INotifyPropertyChanged leaf the card header
  ///     binds to) — never by replacing the entry, whose replace-notification
  ///     would rebuild the card's Expander container mid-count-up (the chevron
  ///     re-animation / cannot-expand dropdown bug). Driven by the view's 80 ms
  ///     timer while busy; silent when the formatted display has not changed, so
  ///     idle time never re-renders the transcript.</summary>
  public void TickToolElapsed()
  {
    if (_runningTool is not { } running)
    {
      return;
    }

    running.Elapsed.Display = ToolElapsed.Format(SecondsNow() - running.StartSeconds);
  }

  private double SecondsNow() => _injectedClock is { } clock ? clock() : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

  private void CloseOpen()
  {
    if (_openIndex >= 0 && Entries[_openIndex] is AssistantTextEntry open)
    {
      Entries[_openIndex] = open with { IsOpen = false }; // markdown rendering begins
    }
    else if (_openIndex >= 0 && Entries[_openIndex] is ReasoningEntry reasoning)
    {
      Entries[_openIndex] = reasoning with { IsOpen = false }; // final markdown render
    }

    _openIndex = -1;
    _openReasoning = null;
  }
}
