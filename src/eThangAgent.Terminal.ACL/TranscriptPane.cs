namespace eThangAgent.Terminal.ACL;

/// <summary>Scroll-back transcript: holds raw message lines and renders the last visible screen rows.</summary>
public sealed class TranscriptPane
{
    private readonly List<string> _lines = new();

    public void AddMessage(string message)
    {
        foreach (var line in message.Split('\n'))
            _lines.Add(line.TrimEnd('\r'));
    }

    public void Render(ITextWriter writer, int top, int height, int width)
    {
        var wrapped = new List<string>();
        foreach (var line in _lines)
            wrapped.AddRange(Wrap(line, width));

        var visible = wrapped.TakeLast(height).ToList();
        for (var row = 0; row < height; row++)
        {
            writer.SetCursorPosition(0, top + row);
            var content = row < visible.Count ? visible[row] : string.Empty;
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
