namespace eThangAgent.Desktop.ViewModels;

/// <summary>What a reference token resolves against.</summary>
internal enum ReferenceTrigger
{
  File,
  Skill,
  Command,
}

/// <summary>One active reference token: <paramref name="Start"/> is the index of the
///     trigger character, <paramref name="Length"/> counts the trigger plus the query,
///     and <paramref name="Query"/> is the text between the trigger and the caret.</summary>
internal sealed record ReferenceToken(ReferenceTrigger Trigger, int Start, int Length, string Query);

/// <summary>Locates the reference token open at a caret position and collapses the
///     <c>@@</c> escape. Pure: no I/O, no state.</summary>
internal static class ReferenceTokenParser
{
  /// <summary>Returns the token the caret sits inside, or null when no token is active.</summary>
  public static ReferenceToken? FindActive(string? input, int caretIndex)
  {
    if (string.IsNullOrEmpty(input) || caretIndex < 0 || caretIndex > input.Length)
    {
      return null;
    }

    if (input.TrimStart().StartsWith('!', StringComparison.Ordinal))
    {
      return null; // shell input is verbatim: no reference resolution
    }

    for (int i = caretIndex - 1; i >= 0; i--)
    {
      char current = input[i];
      if (current == '@')
      {
        return AtToken(input, i, caretIndex);
      }

      if (current == '/')
      {
        if (i == 0)
        {
          return new ReferenceToken(ReferenceTrigger.Command, 0, caretIndex, input[1..caretIndex]);
        }

        continue; // a later slash is a path separator inside the query
      }

      if (!IsTokenCharacter(current))
      {
        return null;
      }
    }

    return null;
  }

  /// <summary>Every <c>@"..."</c> in the message, in order, without the trigger or the
  ///     quotes. A quoted path is the only form that grants a path outside the workspace, so
  ///     this is what the submit path reads to decide what the session may reach.
  ///     <para>THE QUOTES ARE THE POINT. A bare path in prose is a path the user mentioned; a
  ///     quoted one is a path the user named, and only the second is a statement about what
  ///     the session may open. Nothing here decides whether the path EXISTS - an unresolved
  ///     quoted path is reported and the message still sends.</para>
  ///     <para>The contents are taken VERBATIM, spaces and all, which is what makes
  ///     <c>@"C:\Users\me\Downloads\View encrypted message.pdf"</c> one path rather than
  ///     three words. An unterminated quote yields nothing: there is no end to the path, so
  ///     there is no path. <c>@@</c> stays an escaped literal <c>@</c> and never opens a
  ///     quoted path.</para></summary>
  public static List<string> QuotedPaths(string? input)
  {
    List<string> paths = [];
    if (string.IsNullOrEmpty(input))
    {
      return paths;
    }

    int i = 0;
    while (i < input.Length)
    {
      if (input[i] != '@')
      {
        i++;
        continue;
      }

      if (i + 1 < input.Length && input[i + 1] == '@')
      {
        i += 2; // escaped literal: never resolved
        continue;
      }

      if (!TryReadQuotedPath(input, i, out string path, out int next))
      {
        return paths;
      }

      if (path.Length > 0)
      {
        paths.Add(path);
      }

      i = next;
    }

    return paths;
  }

  /// <summary>Reads the quoted path whose trigger sits at <paramref name="at"/>, reporting
  ///     where the scan resumes. Returns false when the quote is unterminated, which ends the
  ///     scan: there is no end to the path, so there is no path.</summary>
  private static bool TryReadQuotedPath(string input, int at, out string path, out int next)
  {
    path = string.Empty;
    next = at + 1;
    if (at + 1 >= input.Length || input[at + 1] != '"')
    {
      return true;
    }

    int close = input.IndexOf('"', at + 2, StringComparison.Ordinal);
    if (close < 0)
    {
      return false;
    }

    next = close + 1;
    if (close > at + 2 && (at == 0 || IsWordBoundary(input[at - 1])))
    {
      path = input[(at + 2)..close];
    }

    return true;
  }

  /// <summary>Collapses every <c>@@</c> escape to a literal <c>@</c>.</summary>
  public static string Unescape(string input)
  {
    ArgumentNullException.ThrowIfNull(input);
    return input.Replace("@@", "@", StringComparison.Ordinal);
  }

  private static ReferenceToken? AtToken(string input, int index, int caretIndex)
  {
    if (index > 0)
    {
      char before = input[index - 1];
      if (before == '@')
      {
        return null; // escaped literal
      }

      if (!IsWordBoundary(before))
      {
        return null; // mid-word @ such as an email address
      }
    }

    return new ReferenceToken(ReferenceTrigger.File, index, caretIndex - index, input[(index + 1)..caretIndex]);
  }

  private static bool IsWordBoundary(char value)
    => char.IsWhiteSpace(value) || value is '(' or '[' or '"';

  private static bool IsTokenCharacter(char value)
    => char.IsLetterOrDigit(value) || value is '-' or '_' or '.' or '/' or '\\';
}
