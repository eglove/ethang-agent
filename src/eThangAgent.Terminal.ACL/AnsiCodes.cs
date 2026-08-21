namespace eThangAgent.Terminal.ACL;

public static class AnsiCodes
{
    public const string EnterAlternateScreen = "\x1b[?1049h\x1b[2J\x1b[H";
    public const string ExitAlternateScreen = "\x1b[?1049l";
    public const string ResetForeground = "\x1b[39m";

    public static string ForegroundColor(ConsoleColor color) => $"\x1b[{Map(color)}m";

    private static int Map(ConsoleColor color) => color switch
    {
        ConsoleColor.Black => 30,
        ConsoleColor.DarkRed => 31,
        ConsoleColor.DarkGreen => 32,
        ConsoleColor.DarkYellow => 33,
        ConsoleColor.DarkBlue => 34,
        ConsoleColor.DarkMagenta => 35,
        ConsoleColor.DarkCyan => 36,
        ConsoleColor.Gray => 37,
        ConsoleColor.DarkGray => 90,
        ConsoleColor.Blue => 94,
        ConsoleColor.Green => 92,
        ConsoleColor.Cyan => 96,
        ConsoleColor.Red => 91,
        ConsoleColor.Magenta => 95,
        ConsoleColor.Yellow => 93,
        ConsoleColor.White => 97,
        _ => 39,
    };
}
