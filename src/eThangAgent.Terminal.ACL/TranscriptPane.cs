namespace eThangAgent.Terminal.ACL;

/// <summary>Scroll-back transcript: holds raw message lines and renders the last visible screen rows.</summary>
public sealed class TranscriptPane
{
    private readonly List<string> _lines = new();
    private bool _streamOpen;

    public void AddMessage(string message)
    {
        _streamOpen = false; // a completed message closes any open streamed message
        foreach (var line in message.Split('\n'))
            _lines.Add(line.TrimEnd('\r'));
    }

    /// <summary>Opens a streamed message: subsequent <see cref="AppendStream"/> calls extend it.
    ///     Starts on a fresh line unless the current last line is an empty separator, which is
    ///     then reused as the stream's first line.</summary>
    public void BeginStream()
    {
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

    /// <summary>Appends reasoning text lines. Reasoning is rendered dim to distinguish it
    ///     from the model's spoken prose; each source line becomes a separate transcript
    ///     entry. Does NOT close the content stream — reasoning interleaves with, not
    ///     replaces, the streaming text.</summary>
    public void AppendReasoning(string text)
    {
        foreach (var line in text.Split('\n'))
            _lines.Add($"[2m{line.TrimEnd('\r')}[0m");
    }

    /// <summary>Appends a tool-call entry with its name and an arguments preview.
    ///     Does NOT close the content stream.</summary>
    public void AppendToolCall(string name, string arguments)
    {
        var argsPreview = arguments.Length > 80 ? arguments[..77] + "…" : arguments;
        _lines.Add($"▸ {name} {{{argsPreview}}}");
    }

    /// <summary>Appends a tool-result entry with its name and a one-line summary.
    ///     Does NOT close the content stream.</summary>
    public void AppendToolResult(string name, string summary)
    {
        var summaryPreview = summary.Length > 100 ? summary[..97] + "…" : summary;
        _lines.Add($"  ↳ {name}: {summaryPreview}");
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
