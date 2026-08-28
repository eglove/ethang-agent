using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.ViewModels;

internal sealed record DesktopCommand(string Name, string Description);

/// <summary>Presentation commands for the desktop frontend.</summary>
internal static class DesktopCommands
{
  private const string Effort = "/effort";
  private const string Model = "/model";
  private static readonly string[] QuitNames = ["/exit", "/quit"];

  public static IReadOnlyList<DesktopCommand> All { get; } =
  [
      new("/effort", "Show or set reasoning effort (z.ai): /effort <level>"),
        new("/exit", "Exit the agent"),
        new("/help", "Show the command list"),
        new("/model", "Show or set the session model (z.ai): /model <model>"),
        new("/quit", "Exit the agent (alias of /exit)"),
        new("/stop", "Interrupt the running turn and all sub-agents"),
    ];

  public static bool IsQuit(string input) => QuitNames.Contains(input);
  public static bool IsHelp(string input) => input == "/help";
  public static bool IsStop(string input) => input == "/stop";

  public static bool IsEffort(string input)
      => input == Effort || input.StartsWith(Effort + " ", StringComparison.Ordinal);

  public static bool IsModel(string input)
      => input == Model || input.StartsWith(Model + " ", StringComparison.Ordinal);

  /// <summary>The argument after /model, or empty when the command was bare.</summary>
  public static string ModelArgument(string input)
      => input[Model.Length..].Trim();

  /// <summary>The argument after /effort, or empty when the command was bare.</summary>
  public static string EffortArgument(string input)
      => input[Effort.Length..].Trim();

  /// <summary>Parses an effort level token — z.ai's exact lowercase vocabulary
  ///     (max, xhigh, high, medium, low, minimal, none).</summary>
  public static bool TryParseEffortLevel(string argument, out ReasoningEffort level)
  {
    level = argument switch
    {
      "max" => ReasoningEffort.Max,
      "xhigh" => ReasoningEffort.ExtraHigh,
      "high" => ReasoningEffort.High,
      "medium" => ReasoningEffort.Medium,
      "low" => ReasoningEffort.Low,
      "minimal" => ReasoningEffort.Minimal,
      "none" => ReasoningEffort.None,
      _ => (ReasoningEffort)(-1),
    };
    return Enum.IsDefined(level);
  }
}
