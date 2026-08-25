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
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return tokens;

        var current = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];

            if (c == ' ' || c == '\t')
            {
                if (inQuotes) current.Append(c);
                else if (tokenStarted)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }
                i++;
                continue;
            }

            if (c == '"')
            {
                tokenStarted = true;
                var backslashes = TrailingBackslashes(current);
                if (backslashes > 0)
                {
                    current.Remove(current.Length - backslashes, backslashes);
                    for (var k = 0; k < backslashes / 2; k++) current.Append('\\');
                    if (backslashes % 2 == 1)
                    {
                        current.Append('"');   // odd run: the quote is literal
                        i++;
                        continue;
                    }
                }
                else if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');       // doubled quote: one literal quote
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;          // even run (or none): toggle quoting
                i++;
                continue;
            }

            tokenStarted = true;
            current.Append(c);
            i++;
        }

        if (tokenStarted) tokens.Add(current.ToString());
        return tokens;
    }

    private static int TrailingBackslashes(StringBuilder sb)
    {
        var n = 0;
        while (n < sb.Length && sb[sb.Length - 1 - n] == '\\') n++;
        return n;
    }
}
