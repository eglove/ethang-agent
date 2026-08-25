namespace eThangAgent.Desktop.ViewModels;

public sealed record DesktopCommand(string Name, string Description);

/// <summary>Presentation commands for the desktop frontend.</summary>
public static class DesktopCommands
{
    private static readonly string[] QuitNames = ["/exit", "/quit"];

    public static IReadOnlyList<DesktopCommand> All { get; } =
    [
        new("/exit", "Exit the agent"),
        new("/help", "Show the command list"),
        new("/quit", "Exit the agent (alias of /exit)"),
        new("/stop", "Interrupt the running turn and all sub-agents"),
    ];

    public static bool IsQuit(string input) => QuitNames.Contains(input);
    public static bool IsHelp(string input) => input == "/help";
    public static bool IsStop(string input) => input == "/stop";
}
