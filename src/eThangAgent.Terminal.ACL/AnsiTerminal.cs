using System.Runtime.InteropServices;

namespace eThangAgent.Terminal.ACL;

/// <summary>
///     ANSI/VT terminal adapter: blocking key reads, cursor operations, ANSI colors,
///     and alternate-screen enter/exit. Enables virtual terminal processing on Windows.
/// </summary>
public sealed class AnsiTerminal : IKeyReader, ITextWriter
{
    static AnsiTerminal()
    {
        try
        {
            EnableVirtualTerminalProcessing();
        }
        catch
        {
            // Non-Windows or redirected host — VT is already available there.
        }
    }

    public ConsoleKeyInfo? ReadKey() => Console.ReadKey(intercept: true);

    // The editor and layout wrap to the visible window, not the (possibly wider) buffer.
    public int BufferWidth => Console.WindowWidth;
    public int CursorLeft => Console.CursorLeft;
    public int CursorTop => Console.CursorTop;

    public void SetCursorPosition(int left, int top) => Console.SetCursorPosition(left, top);

    public void Write(string value) => Console.Write(value);

    public void Write(string value, ConsoleColor foreground) =>
        Console.Write(AnsiCodes.ForegroundColor(foreground) + value + AnsiCodes.ResetForeground);

    public void WriteLine(string value) => Console.WriteLine(value);

    public void EnterAlternateScreen() => Console.Write(AnsiCodes.EnterAlternateScreen);

    public void ExitAlternateScreen() => Console.Write(AnsiCodes.ExitAlternateScreen);

    public void Clear() => Console.Clear();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static void EnableVirtualTerminalProcessing()
    {
        var handle = GetStdHandle(-11);
        if (handle != nint.Zero && GetConsoleMode(handle, out var mode))
            SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
}
