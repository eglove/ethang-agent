using System.Text;
using System.Text.RegularExpressions;

namespace eThangAgent.FileSystem.ACL;

/// <summary>Editorconfig / gitattributes section-glob matching: <c>*</c> stops at a
///     separator, <c>**</c> crosses any depth, <c>?</c> is one non-separator character,
///     and everything else is literal. Patterns are anchored at both ends. A pattern
///     carrying no '/' additionally matches the file NAME at any depth — the spec rule
///     that <c>*.cs</c> applies in every directory, not only the config's own.
///     Compiled patterns are cached process-wide: the same handful of globs recurs on
///     every write.</summary>
internal static class SectionGlob
{
  /// <summary>Bound on a single match. A section glob is author-written text from a
  ///     config file; the timeout keeps a pathological pattern from stalling a write.</summary>
  private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

  private static readonly Dictionary<string, Regex> Cache = [];

  public static bool Matches(string pattern, string relativePath)
  {
    string normalized = relativePath.Replace('\\', '/');
    if (!pattern.Contains('/', StringComparison.Ordinal))
    {
      // Slash-free: the spec's 'any directory' reading. Match the bare relative path
      // too, so a root-level file needs no '**/' prefix in the pattern.
      return IsMatch(pattern, normalized) || IsMatch("**/" + pattern, normalized);
    }

    return IsMatch(pattern, normalized);
  }

  private static bool IsMatch(string pattern, string relativePath)
  {
    try
    {
      return Compile(pattern).IsMatch(relativePath);
    }
    catch (RegexMatchTimeoutException)
    {
      return false; // a pattern too costly to evaluate decides nothing
    }
  }

  private static Regex Compile(string pattern)
  {
    lock (Cache)
    {
      if (Cache.TryGetValue(pattern, out Regex? cached))
      {
        return cached;
      }

      Regex compiled = new(Translate(pattern), RegexOptions.CultureInvariant, MatchTimeout);
      Cache[pattern] = compiled;
      return compiled;
    }
  }

  /// <summary>Renders a section glob as an anchored regular expression.</summary>
  private static string Translate(string pattern)
  {
    StringBuilder regex = new("^");
    int i = 0;
    while (i < pattern.Length)
    {
      int consumed = AppendToken(regex, pattern, i);
      i += consumed;
    }

    _ = regex.Append('$');
    return regex.ToString();
  }

  /// <summary>Appends the regex for the glob token at <paramref name="index"/> and
  ///     reports how many pattern characters it consumed.</summary>
  private static int AppendToken(StringBuilder regex, string pattern, int index)
  {
    char c = pattern[index];
    if (c == '*')
    {
      bool doubled = index + 1 < pattern.Length && pattern[index + 1] == '*';
      if (!doubled)
      {
        _ = regex.Append("[^/]*");
        return 1;
      }

      bool slashAfter = index + 2 < pattern.Length && pattern[index + 2] == '/';
      _ = slashAfter ? regex.Append("(?:.*/)?") : regex.Append(".*");
      return slashAfter ? 3 : 2;
    }

    if (c == '?')
    {
      _ = regex.Append("[^/]");
      return 1;
    }

    _ = regex.Append(Regex.Escape(c.ToString()));
    return 1;
  }
}
