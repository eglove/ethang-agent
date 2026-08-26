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

    StringBuilder current = new();
    bool inQuotes = false;
    bool tokenStarted = false;
    int i = 0;

    while (i < commandLine.Length)
    {
      char c = commandLine[i];

      if (c is ' ' or '\t')
      {
        if (inQuotes)
        {
          _ = current.Append(c);
        }
        else if (tokenStarted)
        {
          tokens.Add(current.ToString());
          _ = current.Clear();
          tokenStarted = false;
        }
        i++;
        continue;
      }

      if (c == '"')
      {
        tokenStarted = true;
        int backslashes = TrailingBackslashes(current);
        if (backslashes > 0)
        {
          _ = current.Remove(current.Length - backslashes, backslashes);
          for (int k = 0; k < backslashes / 2; k++)
          {
            _ = current.Append('\\');
          }

          if (backslashes % 2 == 1)
          {
            _ = current.Append('"');   // odd run: the quote is literal
            i++;
            continue;
          }
        }
        else if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
        {
          _ = current.Append('"');       // doubled quote: one literal quote
          i += 2;
          continue;
        }
        inQuotes = !inQuotes;          // even run (or none): toggle quoting
        i++;
        continue;
      }

      tokenStarted = true;
      _ = current.Append(c);
      i++;
    }

    if (tokenStarted)
    {
      tokens.Add(current.ToString());
    }

    return tokens;
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
