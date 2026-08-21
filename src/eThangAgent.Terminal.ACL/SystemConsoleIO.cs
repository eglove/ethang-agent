using Console = System.Console;

namespace eThangAgent.Terminal.ACL;

/// <summary>System.Console-backed key reader and text writer.</summary>
public sealed class SystemConsoleIO : IKeyReader, ITextWriter
{
    public ConsoleKeyInfo? ReadKey() => Console.ReadKey(intercept: true);

    public int CursorLeft => Console.CursorLeft;
    public int CursorTop => Console.CursorTop;
    public int BufferWidth => Console.BufferWidth;

    public void SetCursorPosition(int left, int top) => Console.SetCursorPosition(left, top);

    public void Write(string value) => Console.Write(value);

    public void Write(string value, ConsoleColor foreground)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = foreground;
        Console.Write(value);
        Console.ForegroundColor = original;
    }

    public void WriteLine(string value) => Console.WriteLine(value);
}
