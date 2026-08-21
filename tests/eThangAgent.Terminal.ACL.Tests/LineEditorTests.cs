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
}
