using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.Terminal.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI.Tests;

public class PipedClarifyChannelTests
{
    private static ClarifyQuestion Question() => new("Which color?", ["red", "green"], true);

    [Fact]
    public async Task NumberLine_PassesThroughVerbatim()
    {
        var result = await new PipedClarifyChannel(new StringReader("2\n"))
            .AskAsync(Question());
        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value);
    }

    [Fact]
    public async Task FreeTextLine_PassesThroughVerbatim()
    {
        var result = await new PipedClarifyChannel(new StringReader("teal-ish blue\n"))
            .AskAsync(Question());
        Assert.True(result.IsSuccess);
        Assert.Equal("teal-ish blue", result.Value);
    }

    [Fact]
    public async Task EndOfInput_ReturnsCancelled()
    {
        var result = await new PipedClarifyChannel(new StringReader(string.Empty))
            .AskAsync(Question());
        Assert.False(result.IsSuccess);
        Assert.Equal("Cancelled", result.Error!.Code);
    }
}

public class InteractiveClarifyChannelTests
{
    private static readonly ClarifyQuestion Question =
        new("Which color?", ["red", "green"], true);

    [Fact]
    public async Task NumberThenEnter_ReturnsDigitAndRendersShortPrompt()
    {
        // The full question + options render in the transcript pane (clarify tool-call
        // entry). The channel itself writes only a short answer prompt that always fits
        // one console row — it must never target a column past the buffer width.
        var writer = new CapturingTextWriter();
        var reader = new ScriptedKeyReader(Key('2'), Key(ConsoleKey.Enter));

        var result = await new InteractiveClarifyChannel(writer, reader).AskAsync(Question);

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value);
        Assert.Contains("answer [1-2]", writer.Text);
    }

    [Fact]
    public async Task Backspace_ErasesPreviousCharacter()
    {
        var writer = new CapturingTextWriter();
        var reader = new ScriptedKeyReader(
            Key('1'), Key('3'), Key(ConsoleKey.Backspace), Key(ConsoleKey.Enter));

        var result = await new InteractiveClarifyChannel(writer, reader).AskAsync(Question);

        Assert.True(result.IsSuccess);
        Assert.Equal("1", result.Value);
    }

    [Fact]
    public async Task CtrlC_ReturnsCancelled()
    {
        var writer = new CapturingTextWriter();
        var reader = new ScriptedKeyReader(
            new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));

        var result = await new InteractiveClarifyChannel(writer, reader).AskAsync(Question);

        Assert.False(result.IsSuccess);
        Assert.Equal("Cancelled", result.Error!.Code);
    }

    [Fact]
    public async Task EndOfKeys_ReturnsCancelled()
    {
        var result = await new InteractiveClarifyChannel(
                new CapturingTextWriter(), new ScriptedKeyReader())
            .AskAsync(Question);

        Assert.False(result.IsSuccess);
        Assert.Equal("Cancelled", result.Error!.Code);
    }

    [Fact]
    public async Task ControlCharacters_AreIgnored()
    {
        var writer = new CapturingTextWriter();
        var reader = new ScriptedKeyReader(
            Key('\u0001'), Key('2'), Key(ConsoleKey.Enter));

        var result = await new InteractiveClarifyChannel(writer, reader).AskAsync(Question);

        Assert.True(result.IsSuccess);
        Assert.Equal("2", result.Value);
    }

    private static ConsoleKeyInfo Key(char c) => new(c, DigitKey(c), false, false, false);

    private static ConsoleKeyInfo Key(ConsoleKey key) =>
        new(key == ConsoleKey.Enter ? '\r' : '\0', key, false, false, false);

    private static ConsoleKey DigitKey(char c) => c switch
    {
        '1' => ConsoleKey.D1,
        '2' => ConsoleKey.D2,
        '3' => ConsoleKey.D3,
        _ => ConsoleKey.NoName,
    };

    private sealed class ScriptedKeyReader(params ConsoleKeyInfo[] keys) : IKeyReader
    {
        private int _index;
        public ConsoleKeyInfo? ReadKey() => _index < keys.Length ? keys[_index++] : null;
    }

    private sealed class CapturingTextWriter : ITextWriter
    {
        private readonly StringBuilder _text = new();

        public string Text => _text.ToString();
        public int CursorLeft => 0;
        public int CursorTop => 0;
        public int BufferWidth => 80;

        public void SetCursorPosition(int left, int top) { }
        public void Write(string value) => _text.Append(value);
        public void Write(string value, ConsoleColor foreground) => _text.Append(value);
        public void WriteLine(string value) => _text.AppendLine(value);
    }
}
