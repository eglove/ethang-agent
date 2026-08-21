namespace eThangAgent.CLI;

public sealed record CliCommand(string Name, string Description);

/// <summary>Single source of truth for the CLI's slash commands: the registry, quit/help matching, and the formatted command list.</summary>
public static class CliCommands
{
    private static readonly string[] QuitNames = ["/exit", "/quit"];

    public static IReadOnlyList<CliCommand> All { get; } =
    [
        new CliCommand("/exit", "Exit the agent"),
        new CliCommand("/help", "Show the command list"),
        new CliCommand("/quit", "Exit the agent (alias of /exit)"),
    ];

    public static bool IsQuit(string input) => QuitNames.Contains(input);

    public static bool IsHelp(string input) => input == "/help";

    public static string Describe()
    {
        var lines = All
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => $"  {c.Name}  —  {c.Description}");
        return "Commands:" + string.Join("", lines.Select(l => "\n" + l));
    }
}
