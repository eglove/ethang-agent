namespace eThangAgent.Desktop.ViewModels;

/// <summary>One closed session command: the name typed after <c>/</c> and the line the
///     popup shows for it.</summary>
internal sealed record SessionCommand(string Name, string Description);

/// <summary>The closed registry of session commands the composer's <c>/</c> trigger resolves.
///     A name outside this list is not a command, so the submit path treats the input as an
///     ordinary message.</summary>
internal static class SessionCommandRegistry
{
  private static readonly SessionCommand[] Commands =
  [
    new("new", "Start a new session in this workspace with the rest of the input as its first prompt."),
  ];

  /// <summary>Every registered command, in declaration order.</summary>
  public static IReadOnlyList<SessionCommand> All { get; } = Commands;

  /// <summary>The command named <paramref name="name"/> (case-insensitive), or null when the
  ///     registry does not carry it.</summary>
  public static SessionCommand? Find(string name)
  {
    ArgumentNullException.ThrowIfNull(name);
    return Commands.FirstOrDefault(
        command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));
  }
}
