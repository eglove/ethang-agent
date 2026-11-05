namespace eThangAgent.Lsp.ACL;

/// <summary>What the language server is doing, as the daemon announces it on its own
///     stdout. Three states and no fourth: a server is being fetched, it is serving, or
///     there is none - a workspace with no code never announces anything at all and stays
///     silent rather than claiming a state it never reached.</summary>
public enum LspServerInstallState
{
  /// <summary>No announcement has been read yet, so nothing is known and nothing shows.</summary>
  Unknown,

  /// <summary>The daemon is resolving or fetching the server: a one-time download.</summary>
  Downloading,

  /// <summary>The server resolved and is launching.</summary>
  Ready,

  /// <summary>The server could not be resolved or installed.</summary>
  Unavailable,
}

/// <summary>Reads the daemon's stdout announcements into <see cref="LspServerInstallState"/>.
///     The daemon writes <c>server-resolving &lt;workspaceId&gt;</c>, <c>server-ready
///     &lt;version&gt;</c>, and <c>server-unavailable</c>; every other line it writes is
///     lifecycle chatter that says nothing about the server.</summary>
public static class LspServerInstallLine
{
  private const string ResolvingPrefix = "server-resolving";
  private const string ReadyPrefix = "server-ready";
  private const string Unavailable = "server-unavailable";

  /// <summary>True when <paramref name="line"/> is a server announcement, which it then
  ///     describes through <paramref name="state"/> and <paramref name="detail"/> (the
  ///     version for a ready line, empty otherwise).</summary>
  public static bool TryParse(string? line, out LspServerInstallState state,
      out string detail)
  {
    detail = string.Empty;
    state = LspServerInstallState.Unknown;
    if (string.IsNullOrWhiteSpace(line))
    {
      return false;
    }

    string trimmed = line.Trim();
    if (string.Equals(trimmed, Unavailable, StringComparison.Ordinal))
    {
      state = LspServerInstallState.Unavailable;
      return true;
    }

    if (TryReadArgument(trimmed, ResolvingPrefix, out _))
    {
      state = LspServerInstallState.Downloading;
      return true;
    }

    if (TryReadArgument(trimmed, ReadyPrefix, out string version))
    {
      state = LspServerInstallState.Ready;
      detail = version;
      return true;
    }

    return false;
  }

  /// <summary>Matches <c>prefix argument</c> exactly once: the prefix must be followed by
  ///     a separator and a non-empty argument, so a bare prefix is not an announcement.</summary>
  private static bool TryReadArgument(string line, string prefix, out string argument)
  {
    argument = string.Empty;
    if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length <= prefix.Length)
    {
      return false;
    }

    if (line[prefix.Length] != ' ')
    {
      return false;
    }

    argument = line[(prefix.Length + 1)..].Trim();
    return argument.Length > 0;
  }
}
