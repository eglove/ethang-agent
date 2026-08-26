using System.Collections.ObjectModel;
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
