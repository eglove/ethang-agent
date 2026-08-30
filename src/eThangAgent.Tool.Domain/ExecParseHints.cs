namespace eThangAgent.ToolDomain;

/// <summary>Post-failure diagnostic hints for exec parse errors. Scans the program
///     text for the recurring raw-string mistakes that Roslyn reports as misleading
///     generic syntax errors ('; expected', ') expected'), and produces short
///     actionable hint lines rendered above the raw diagnostics. Runs only on the
///     parse-failure path - never on a successful compile.</summary>
public static class ExecParseHints
{
  public static IReadOnlyList<string> Analyze(string programText)
  {
    ArgumentNullException.ThrowIfNull(programText);
    List<string> hints = [];
    string[] lines = programText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    bool insideRaw = false;
    for (int i = 0; i < lines.Length; i++)
    {
      string line = lines[i];
      int run = MaxQuoteRun(line);
      if (run > 3)
      {
        hints.Add($"hint (line {i + 1}): raw-string delimiter conflict - never type more than 3 quote characters in a row; build multi-line content as a string array joined with newlines");
        continue;
      }

      if (run < 3)
      {
        continue;
      }

      int start = line.IndexOf('\"', StringComparison.Ordinal);
      string after = line[(start + 3)..].TrimStart();
      if (!insideRaw)
      {
        if (after.Length > 0 && after[0] is not ';' and not ')' and not ',' and not ']')
        {
          hints.Add($"hint (line {i + 1}): the opening raw-string delimiter must be followed by a newline - put content on the next line");
        }

        insideRaw = true;
        continue;
      }

      // Inside a raw string, a triple-quote line is a closing attempt: it must have
      // no prefix and only terminators after it.
      if (start > 0 || (after.Length > 0 && after[0] is not ';' and not ')' and not ',' and not ']'))
      {
        hints.Add($"hint (line {i + 1}): a closing raw-string delimiter must start its own line (no leading whitespace or code before it)");
      }

      insideRaw = false;
    }

    return hints;
  }

  private static int MaxQuoteRun(string line)
  {
    int best = 0;
    int current = 0;
    foreach (char c in line)
    {
      if (c == '\"')
      {
        current++;
        best = Math.Max(best, current);
      }
      else
      {
        current = 0;
      }
    }

    return best;
  }
}
