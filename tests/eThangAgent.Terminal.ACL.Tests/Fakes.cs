using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public sealed class FakeKeyReader(params ConsoleKeyInfo[] keys) : IKeyReader
{
    private int _index;

    public ConsoleKeyInfo? ReadKey() => _index < keys.Length ? keys[_index++] : null;
}

public sealed class FakeWriter : ITextWriter
{
    public List<(string Text, ConsoleColor? Color)> Writes { get; } = new();

    public List<(int Left, int Top, string Text)> RowWrites { get; } = new();

    public int CursorLeft { get; private set; }
    public int CursorTop { get; private set; }

    public int BufferWidth { get; set; } = 80;

    public List<(int Left, int Top)> Moves { get; } = new();

    public void SetCursorPosition(int left, int top)
    {
        Moves.Add((left, top));
        CursorLeft = left;
        CursorTop = top;
    }

    public void Write(string value)
    {
        Writes.Add((value, null));
        RowWrites.Add((CursorLeft, CursorTop, value));
        CursorLeft += value.Length;
    }

    public void Write(string value, ConsoleColor foreground)
    {
        Writes.Add((value, foreground));
        RowWrites.Add((CursorLeft, CursorTop, value));
        CursorLeft += value.Length;
    }

    public void WriteLine(string value)
    {
        Writes.Add((value + Environment.NewLine, null));
        CursorLeft = 0;
        CursorTop++;
    }

    public string AllText => string.Concat(Writes.Select(w => w.Text));
}
