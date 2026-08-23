using eThangAgent.SharedKernel;

namespace eThangAgent.Terminal.ACL;

/// <summary>Scroll-back transcript: holds raw message lines and renders the last visible screen rows.</summary>
public sealed class TranscriptPane
{
    private const string Dim = "\u001b[2m";
    private const string ResetDim = "\u001b[0m";

    private readonly List<string> _lines = new();
    private bool _streamOpen;

    // Open reasoning block: contiguous dim lines re-rendered in place as deltas arrive,
    // so fragmented provider output collapses instead of stacking one line per fragment.
    private StreamedTextNormalizer? _reasoning;
    private int _reasoningStart;
    private int _reasoningLineCount;

    public void AddMessage(string message)
    {
        CloseReasoningBlock();
        _streamOpen = false; // a completed message closes any open streamed message
        foreach (var line in message.Split('\n'))
            _lines.Add(line.TrimEnd('\r'));
    }

    /// <summary>Opens a streamed message: subsequent <see cref="AppendStream"/> calls extend it.
    ///     Starts on a fresh line unless the current last line is an empty separator, which is
    ///     then reused as the stream's first line.</summary>
    public void BeginStream()
    {
        CloseReasoningBlock();
        _streamOpen = true;
        if (_lines.Count == 0 || _lines[^1].Length > 0)
            _lines.Add(string.Empty);
    }

    /// <summary>Extends the open streamed message with one delta chunk; embedded newlines split
    ///     lines exactly as AddMessage splits them. No effect while no stream is open.</summary>
    public void AppendStream(string delta)
    {
        if (!_streamOpen)
            return;
        var parts = delta.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                _lines.Add(string.Empty);
            _lines[^1] += parts[i];
        }
    }

    /// <summary>Appends reasoning text. Reasoning renders dim to distinguish it from the model's
    ///     spoken prose and passes through the shared <see cref="StreamedTextNormalizer"/>, so
    ///     mid-word wraps join and blank-line floods collapse. The open block's lines are
    ///     rewritten in place on every delta. Does NOT close the content stream — reasoning
    ///     interleaves with, not replaces, the streaming text.</summary>
    public void AppendReasoning(string text)
    {
        if (_reasoning is null)
            _reasoningStart = _lines.Count;
        _reasoning ??= new StreamedTextNormalizer();
        _reasoning.Append(text);

        var normalized = _reasoning.Text;
        var lines = normalized.Length == 0
            ? []
            : normalized.Split('\n')
                .Select(l => l.Length == 0 ? l : $"{Dim}{l.TrimEnd('\r')}{ResetDim}")
                .ToArray();

        if (_reasoningLineCount > 0)
            _lines.RemoveRange(_reasoningStart, _reasoningLineCount);
        _lines.InsertRange(_reasoningStart, lines);
        _reasoningLineCount = lines.Length;
    }

    private void CloseReasoningBlock()
    {
        _reasoning = null;
        _reasoningLineCount = 0;
    }

    /// <summary>Appends a tool-call entry with its name and an arguments preview.
    ///     Does NOT close the content stream.</summary>
    public void AppendToolCall(string name, string arguments)
    {
        CloseReasoningBlock();
        var argsPreview = arguments.Length > 400 ? arguments[..397] + "\u2026" : arguments;
        _lines.Add($"\u25b8 {name} {{{argsPreview}}}");
    }

    /// <summary>Appends a tool-result entry with its name and a one-line summary.
    ///     Does NOT close the content stream.</summary>
    public void AppendToolResult(string name, string summary)
    {
        CloseReasoningBlock();
        var summaryPreview = summary.Length > 100 ? summary[..97] + "\u2026" : summary;
        _lines.Add($"  \u21b3 {name}: {summaryPreview}");
    }

    public void Render(ITextWriter writer, int top, int height, int width)
    {
        var wrapped = new List<string>();
        foreach (var line in _lines)
            wrapped.AddRange(Wrap(line, width));

        var visible = wrapped.TakeLast(height).ToList();
        var offset = height - visible.Count; // anchor content to the bottom: short conversations hug the input row
        for (var row = 0; row < height; row++)
        {
            writer.SetCursorPosition(0, top + row);
            var index = row - offset;
            var content = index >= 0 && index < visible.Count ? visible[index] : string.Empty;
            writer.Write(content.PadRight(width)[..width]);
        }
    }

    private static List<string> Wrap(string text, int width)
    {
        var rows = new List<string>();
        foreach (var source in text.Split('\n'))
        {
            if (width < 1)
            {
                rows.Add(source);
                continue;
            }

            var current = string.Empty;
            foreach (var raw in source.Split(' '))
            {
                var word = raw;
                while (word.Length > width)
                {
                    if (current.Length > 0)
                    {
                        rows.Add(current);
                        current = string.Empty;
                    }
                    rows.Add(word[..width]);
                    word = word[width..];
                }

                if (current.Length == 0)
                    current = word;
                else if (current.Length + 1 + word.Length <= width)
                    current += " " + word;
                else
                {
                    rows.Add(current);
                    current = word;
                }
            }

            rows.Add(current);
        }
        return rows;
    }
}
