namespace eThangAgent.ToolDomain.Verification;

/// <summary>Decides whether one completed shell run is verification-class:
///     the command tokens match a configured prefix (exe plus optional
///     argument tokens) AND the exit code is 0. A failed run never counts.</summary>
public sealed class VerificationCommandSpecification
{
  private readonly string[][] _prefixes;

  public VerificationCommandSpecification(IReadOnlyList<string> commandPrefixes)
  {
    ArgumentNullException.ThrowIfNull(commandPrefixes);
    if (commandPrefixes.Count == 0)
    {
      throw new ArgumentOutOfRangeException(nameof(commandPrefixes),
          "At least one verification command prefix is required.");
    }

    _prefixes = [.. commandPrefixes.Select(SplitTokens)];
    foreach (string[] prefix in _prefixes)
    {
      if (prefix.Length == 0)
      {
        throw new ArgumentOutOfRangeException(nameof(commandPrefixes),
            "A verification command prefix must contain at least one token.");
      }
    }
  }

  /// <summary>True when the record's exit code is 0 and its command tokens
  ///     start with one of the configured prefixes.</summary>
  public bool IsSatisfiedBy(ShellExecutionRecord record)
  {
    ArgumentNullException.ThrowIfNull(record);
    return record.ExitCode == 0 && _prefixes.Any(p => MatchesPrefix(record.Tokens, p));
  }

  private static bool MatchesPrefix(IReadOnlyList<string> tokens, string[] prefix)
  {
    if (tokens.Count < prefix.Length || !ExeEquals(tokens[0], prefix[0]))
    {
      return false;
    }

    for (int i = 1; i < prefix.Length; i++)
    {
      if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
      {
        return false;
      }
    }

    return true;
  }

  /// <summary>Exe tokens compare by file basename, case-insensitively, with a
  ///     trailing '.exe' stripped on both sides: a full-path dotnet.exe
  ///     invocation matches the bare 'dotnet' prefix.</summary>
  private static bool ExeEquals(string actual, string prefixExe) =>
      string.Equals(BaseName(actual), BaseName(prefixExe), StringComparison.OrdinalIgnoreCase);

  private static string BaseName(string exe)
  {
    string name = Path.GetFileName(exe.Trim());
    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
  }

  private static string[] SplitTokens(string prefix) =>
      [.. prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
