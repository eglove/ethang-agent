using System.Collections.ObjectModel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>
/// Holds rendered transcript entries and applies stream events with the same
/// semantics as the terminal DrainStream: deltas extend the open block; iteration end
/// (or any non-stream event) closes it so the next delta opens a fresh entry. All methods
/// run on the UI thread — callers marshal (Task 9 bridge).
/// </summary>
public sealed class TranscriptViewModel
{
    private readonly ObservableCollection<TranscriptEntry> _entries = [];

    // Index of the extendable AssistantTextEntry / ReasoningEntry, else -1.
    private int _openIndex = -1;

    public ObservableCollection<TranscriptEntry> Entries => _entries;

    public void AddUser(string text)
    {
        CloseOpen();
        _entries.Add(new UserMessageEntry(text));
    }

    public void AddToolCall(string name, string arguments)
    {
        CloseOpen();
        _entries.Add(new ToolCallEntry(name, arguments));
    }

    public void AddToolResult(string name, string summary)
    {
        CloseOpen();
        _entries.Add(new ToolResultEntry(name, summary));
    }

    public void AddNotice(string text)
    {
        CloseOpen();
        _entries.Add(new NoticeEntry(text));
    }

    public void EndIteration() => CloseOpen();

    public void AppendAssistantDelta(string text)
    {
        if (_openIndex >= 0 && _entries[_openIndex] is AssistantTextEntry open)
        {
            // Replace notification drives re-render.
            _entries[_openIndex] = open with { Text = open.Text + text };
            return;
        }

        CloseOpen();
        _entries.Add(new AssistantTextEntry(text));
        _openIndex = _entries.Count - 1;
    }

    public void AppendReasoning(string text)
    {
        if (_openIndex >= 0 && _entries[_openIndex] is ReasoningEntry open)
        {
            _entries[_openIndex] = open with { Text = open.Text + text };
            return;
        }

        CloseOpen();
        _entries.Add(new ReasoningEntry(text));
        _openIndex = _entries.Count - 1;
    }

    private void CloseOpen() => _openIndex = -1;
}
