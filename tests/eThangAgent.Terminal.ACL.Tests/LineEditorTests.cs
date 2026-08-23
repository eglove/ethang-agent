using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class LineEditorTests
{
    private static ConsoleKeyInfo CharKey(char c) => new(c, ConsoleKey.None, false, false, false);

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    private static string? Type(params ConsoleKeyInfo[] keys)
    {
        var editor = new LineEditor(new FakeKeyReader(keys), new FakeWriter());
        return editor.Read("> ");
    }

    [Fact]
    public void TypingCharsThenEnter_ReturnsLine()
    {
        Assert.Equal("hello", Type(CharKey('h'), CharKey('e'), CharKey('l'), CharKey('l'), CharKey('o'), Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void LeftArrow_ThenChar_InsertsAtCursor()
    {
        Assert.Equal("acb", Type(CharKey('a'), CharKey('b'), Key(ConsoleKey.LeftArrow), CharKey('c'), Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void Backspace_RemovesCharBeforeCursor()
    {
        Assert.Equal("a", Type(CharKey('a'), CharKey('b'), Key(ConsoleKey.Backspace), Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void Delete_RemovesCharAtCursor()
    {
        Assert.Equal("a", Type(CharKey('a'), CharKey('b'), Key(ConsoleKey.LeftArrow), Key(ConsoleKey.Delete), Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void Home_And_End_MoveCursorToLineEdges()
    {
        Assert.Equal("xabcy", Type(CharKey('a'), CharKey('b'), CharKey('c'), Key(ConsoleKey.Home), CharKey('x'), Key(ConsoleKey.End), CharKey('y'), Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void UpArrow_RecallsPreviousInput()
    {
        var history = new List<string>();
        var editor = new LineEditor(
            new FakeKeyReader(
                CharKey('a'), CharKey('l'), CharKey('p'), CharKey('h'), CharKey('a'), Key(ConsoleKey.Enter),
                Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter)),
            new FakeWriter());

        Assert.Equal("alpha", editor.Read("> ", history, null));
        Assert.Equal("alpha", editor.Read("> ", history, null));
    }

    [Fact]
    public void UpThenDown_ReturnsToDraft()
    {
        var history = new List<string>();
        var editor = new LineEditor(
            new FakeKeyReader(
                CharKey('a'), CharKey('l'), CharKey('p'), CharKey('h'), CharKey('a'), Key(ConsoleKey.Enter),
                CharKey('d'), CharKey('r'), CharKey('a'), CharKey('f'), CharKey('t'), Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow), CharKey('b'), Key(ConsoleKey.Enter)),
            new FakeWriter());

        Assert.Equal("alpha", editor.Read("> ", history, null));
        Assert.Equal("draftb", editor.Read("> ", history, null));
    }

    [Fact]
    public void Tab_AcceptsCompletion()
    {
        var completer = new PrefixAutoCompleter(["/exit", "/help", "/quit"]);
        var editor = new LineEditor(
            new FakeKeyReader(CharKey('/'), CharKey('e'), Key(ConsoleKey.Tab), Key(ConsoleKey.Enter)),
            new FakeWriter());

        Assert.Equal("/exit", editor.Read("> ", null, completer));
    }

    [Fact]
    public void GhostSuggestion_IsRenderedWhileTyping()
    {
        var completer = new PrefixAutoCompleter(["/exit", "/help", "/quit"]);
        var writer = new FakeWriter();
        var editor = new LineEditor(new FakeKeyReader(CharKey('/'), CharKey('e')), writer);

        editor.Read("> ", null, completer);

        Assert.Contains("/exit", writer.AllText);
    }

    [Fact]
    public void CtrlC_ClearsLine_AndReturnsEmpty()
    {
        Assert.Equal(string.Empty, Type(CharKey('a'), Ctrl(ConsoleKey.C)));
    }

    [Fact]
    public void CtrlD_OnEmptyLine_ReturnsNull()
    {
        Assert.Null(Type(Ctrl(ConsoleKey.D)));
    }

    [Fact]
    public void ExhaustedKeysWithoutEnter_ReturnsNull()
    {
        Assert.Null(Type(CharKey('h'), CharKey('i')));
    }

    [Fact]
    public void Prompt_IsWrittenBeforeInput()
    {
        var writer = new FakeWriter();
        var editor = new LineEditor(new FakeKeyReader(Key(ConsoleKey.Enter)), writer);

        editor.Read("> ");

        Assert.StartsWith("> ", writer.AllText);
    }

    private sealed class StrictWriter : ITextWriter
    {
        public StrictWriter(int width, int height)
        {
            BufferWidth = width;
            BufferHeight = height;
            CursorLeft = 0;
            // The TUI input row: one row above the statusline, i.e. the second-to-last row.
            CursorTop = height - 2;
        }

        public int BufferWidth { get; }
        public int BufferHeight { get; }
        public int CursorLeft { get; private set; }
        public int CursorTop { get; private set; }

        public List<(int Left, int Top)> Moves { get; } = new();

        public void SetCursorPosition(int left, int top)
        {
            // Same contract as Console.SetCursorPosition: any cell outside the buffer throws.
            if (left < 0 || top < 0 || left >= BufferWidth || top >= BufferHeight)
                throw new ArgumentOutOfRangeException(nameof(left),
                    $"cell ({left},{top}) is outside the {BufferWidth}x{BufferHeight} console buffer");
            Moves.Add((left, top));
            CursorLeft = left;
            CursorTop = top;
        }

        public void Write(string value) => CursorLeft += value.Length;

        public void Write(string value, ConsoleColor foreground) => CursorLeft += value.Length;

        public void WriteLine(string value)
        {
            CursorLeft = 0;
            CursorTop++;
        }
    }

    [Fact]
    public void LongLineBeyondBufferBottom_DoesNotCrash()
    {
        // Regression: a line longer than the input row used to wrap past the bottom of the
        // console buffer, and SetCursorPosition threw ArgumentOutOfRangeException, killing
        // the REPL. The editor must scroll horizontally within its single row instead.
        var writer = new StrictWriter(width: 20, height: 24);
        var keys = Enumerable.Range(0, 50).Select(_ => CharKey('x')).Append(Key(ConsoleKey.Enter)).ToArray();
        var editor = new LineEditor(new FakeKeyReader(keys), writer);

        var line = editor.Read("> ", null, null);

        Assert.Equal(new string('x', 50), line);
    }

    [Fact]
    public void LongLine_CursorNeverLeavesInputRow()
    {
        var writer = new StrictWriter(width: 20, height: 24);
        var startTop = writer.CursorTop;
        var keys = Enumerable.Range(0, 40).Select(_ => CharKey('x'))
            .Concat(Enumerable.Range(0, 25).Select(_ => Key(ConsoleKey.LeftArrow)))
            .Append(Key(ConsoleKey.Enter))
            .ToArray();
        var editor = new LineEditor(new FakeKeyReader(keys), writer);

        var line = editor.Read("> ", null, null);

        Assert.Equal(new string('x', 40), line);
        Assert.NotEmpty(writer.Moves);
        Assert.All(writer.Moves, m => Assert.Equal(startTop, m.Top));
        Assert.All(writer.Moves, m => Assert.InRange(m.Left, 0, writer.BufferWidth - 1));
    }
}
