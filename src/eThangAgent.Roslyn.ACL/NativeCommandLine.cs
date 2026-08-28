using System.Text;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Splits a Windows command line into argv tokens following
/// CommandLineToArgvW semantics: whitespace separates tokens outside quotes;
/// quoted segments stay one token (empty ones included); a doubled quote inside
/// a quoted segment is one literal quote; a run of N backslashes before a quote
/// becomes floor(N/2) backslashes, and an odd run makes the quote literal.
/// This replaces the former powershell -EncodedCommand hop: the re-parse happens
/// in-process and the process itself is spawned directly.</summary>
public static class NativeCommandLine
{
  public static IReadOnlyList<string> Split(string commandLine)
  {
    List<string> tokens = [];
    if (string.IsNullOrEmpty(commandLine))
    {
      return tokens;
    }

    TokenAssembler assembler = new(tokens);
    int i = 0;
    while (i < commandLine.Length)
    {
      i = assembler.Advance(commandLine, i);
    }

    assembler.FlushTrailingToken();
    return tokens;
  }

  /// <summary>The mutable quote/whitespace state of one in-progress split. The main
  ///     loop delegates each character here so <see cref="Split"/> reads as the Win32
  ///     algorithm: whitespace, quote, or literal, consumed left to right.</summary>
  private sealed class TokenAssembler(List<string> tokens)
  {
    private readonly StringBuilder _current = new();
    private readonly List<string> _tokens = tokens;
    private bool _inQuotes;
    private bool _tokenStarted;

    /// <summary>Consumes the character at <paramref name="i"/> and returns the next index.</summary>
    internal int Advance(string commandLine, int i)
    {
      char c = commandLine[i];
      if (c is ' ' or '\t')
      {
        return AdvanceWhitespace(c, i);
      }

      int next = c == '"'
        ? AdvanceQuote(commandLine, i)
        : AdvanceLiteral(c, i);
      return next;
    }

    private int AdvanceWhitespace(char c, int i)
    {
      if (_inQuotes)
      {
        _ = _current.Append(c);
      }
      else if (_tokenStarted)
      {
        EndToken();
      }

      return i + 1;
    }

    private int AdvanceLiteral(char c, int i)
    {
      _tokenStarted = true;
      _ = _current.Append(c);
      return i + 1;
    }

    /// <summary>Quote handling: a run of N backslashes before the quote becomes
    ///     floor(N/2) backslashes (an odd run makes the quote literal); a doubled
    ///     quote inside a quoted segment is one literal quote; otherwise toggle quoting.</summary>
    private int AdvanceQuote(string commandLine, int i)
    {
      _tokenStarted = true;
      int backslashes = TrailingBackslashes(_current);
      if (backslashes > 0)
      {
        return AdvanceEscapedQuote(backslashes, i);
      }

      if (_inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
      {
        _ = _current.Append('"');       // doubled quote: one literal quote
        return i + 2;
      }

      _inQuotes = !_inQuotes;          // even run (or none): toggle quoting
      return i + 1;
    }

    private int AdvanceEscapedQuote(int backslashes, int i)
    {
      _ = _current.Remove(_current.Length - backslashes, backslashes);
      for (int k = 0; k < backslashes / 2; k++)
      {
        _ = _current.Append('\\');
      }

      if (backslashes % 2 == 1)
      {
        _ = _current.Append('"');   // odd run: the quote is literal
        return i + 1;
      }

      _inQuotes = !_inQuotes;       // even run: toggle quoting
      return i + 1;
    }

    internal void FlushTrailingToken()
    {
      if (_tokenStarted)
      {
        _tokens.Add(_current.ToString());
      }
    }

    private void EndToken()
    {
      _tokens.Add(_current.ToString());
      _ = _current.Clear();
      _tokenStarted = false;
    }

    private static int TrailingBackslashes(StringBuilder sb)
    {
      int n = 0;
      while (n < sb.Length && sb[sb.Length - 1 - n] == '\\')
      {
        n++;
      }

      return n;
    }
  }
}
