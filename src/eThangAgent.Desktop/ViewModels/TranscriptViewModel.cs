using System.Collections.ObjectModel;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>
/// Holds rendered transcript entries and applies stream events with the same
/// semantics as the terminal DrainStream: deltas extend the open block; iteration end
/// (or any non-stream event) closes it so the next delta opens a fresh entry. All methods
/// run on the UI thread — callers marshal (Task 9 bridge).
/// </summary>
internal sealed class TranscriptViewModel
{

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
    Entries.Add(new ToolCallEntry(name, arguments));
  }

  public void AddToolResult(string name, string summary)
  {
    CloseOpen();
    Entries.Add(new ToolResultEntry(name, summary));
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
          RestoreToolResult(callNames, message);
          break;
        case Role.System:
          AddNotice(message.Content);
          break;
        default:
          break;
      }
    }

    CloseOpen();
  }

  /// <summary>Restores one assistant message: its text (when non-empty) followed by one
  ///     call entry per requested tool call, recording names so the following results
  ///     can resolve theirs.</summary>
  private void RestoreAssistant(Dictionary<string, string> callNames, Message message)
  {
    CloseOpen();
    if (message.Content.Length > 0)
    {
      Entries.Add(new AssistantTextEntry(message.Content));
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

  /// <summary>Restores one tool result, naming it from the preceding assistant batch by
  ///     tool-call id ("tool" when unresolvable).</summary>
  private void RestoreToolResult(Dictionary<string, string> callNames, Message message)
  {
    AddToolResult(
        message.ToolCallId is not null && callNames.TryGetValue(message.ToolCallId, out string? name)
            ? name
            : "tool",
        message.Content);
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
      Entries[_openIndex] = open with { Text = open.Text + text };
      return;
    }

    CloseOpen();
    Entries.Add(new AssistantTextEntry(text));
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
      Entries[_openIndex] = open with { Text = _openReasoning.Text };
      return;
    }

    CloseOpen();
    StreamedTextNormalizer normalizer = new();
    normalizer.Append(text);
    _openReasoning = normalizer;
    Entries.Add(new ReasoningEntry(normalizer.Text));
    _openIndex = Entries.Count - 1;
  }

  private void CloseOpen()
  {
    _openIndex = -1;
    _openReasoning = null;
  }
}
