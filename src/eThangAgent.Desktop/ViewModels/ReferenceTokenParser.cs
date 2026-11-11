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
