using System.Text.RegularExpressions;

namespace eThangAgent.SkillDomain;

/// <summary>How a scan finding affects the install (spec #27): Block aborts
/// unconditionally; Advisory stops the install until the user confirms.</summary>
public enum FindingSeverity
{
  Block,
  Advisory,
}

/// <summary>One scanner finding: what rule matched, in which file, on which
/// 1-based line.</summary>
public sealed record ScanFinding(FindingSeverity Severity, string Rule, string FilePath, int LineNumber);

/// <summary>The scan outcome for one file or one skill folder.</summary>
public sealed record ScanResult(IReadOnlyList<ScanFinding> Findings)
{
  public bool HasBlock => Findings.Any(f => f.Severity == FindingSeverity.Block);
  public bool HasAdvisory => Findings.Any(f => f.Severity == FindingSeverity.Advisory);
}

/// <summary>Deterministic install-time content scanner (spec #27): compiled
/// regexes over file text, no model calls, no network. The pattern lists are
/// data (static arrays) and extend without touching the pipeline. Positive
/// AND negative cases are pinned by unit tests — ordinary skill prose that
/// merely mentions keys, tokens, or curl MUST NOT trip the scanner.</summary>
public static class SkillContentScanner
{
  private const int NullByteProbeBytes = 8 * 1024;
  private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

  private static readonly (string Rule, Regex Pattern)[] BlockPatterns =
  [
    ("ExfiltrateEnv",
        new Regex(@"\b(send|post|upload|exfiltrate|curl|wget)\b.{0,40}\b(env|environment|api[ _-]?keys?|tokens?|secrets?|credentials)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
    ("IgnoreInstructions",
        new Regex(@"\b(ignore|disregard|forget)\b.{0,30}\b(previous|prior|above|all)\b.{0,20}\b(instructions?|prompts?|rules?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
    ("FetchAndExecute",
        new Regex(@"\b(curl|wget|iwr|invoke-webrequest|irm)\b.{0,60}\|\s*(sudo\s+)?(sh|bash|powershell|iex|invoke-expression)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)),
  ];

  private static readonly (string Rule, Regex Pattern)[] AdvisoryPatterns =
  [
    ("CredentialShaped",
        new Regex(@"\b(sk-[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16})\b",
            RegexOptions.Compiled, MatchTimeout)),
    ("ShellOneLiner",
        new Regex(@"(^|\s)rm -rf /(\s|$)|(^|\s):\(\)\{ :\|:& \};:",
            RegexOptions.Compiled, MatchTimeout)),
    ("Base64Blob",
        new Regex(@"[A-Za-z0-9+/]{200,}={0,2}",
            RegexOptions.Compiled, MatchTimeout)),
  ];

  /// <summary>Scans one text file. LineNumber is 1-based over \n and \r\n.</summary>
  public static ScanResult ScanFile(string relativePath, string content)
  {
    ArgumentNullException.ThrowIfNull(content);
    List<ScanFinding> findings = [];
    string[] lines = content.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
      int lineNumber = i + 1;
      foreach ((string rule, Regex pattern) in BlockPatterns)
      {
        if (pattern.IsMatch(lines[i]))
        {
          findings.Add(new ScanFinding(FindingSeverity.Block, rule, relativePath, lineNumber));
        }
      }

      foreach ((string rule, Regex pattern) in AdvisoryPatterns)
      {
        if (pattern.IsMatch(lines[i]))
        {
          findings.Add(new ScanFinding(FindingSeverity.Advisory, rule, relativePath, lineNumber));
        }
      }
    }

    return new ScanResult(findings);
  }

  /// <summary>Binary detection for the install scan (ledger v54): known
  /// binary extensions, else a null byte in the first 8 KB of the head.
  /// The head is at most the first 8 KB the caller read.</summary>
  public static bool LooksBinary(string extension, ReadOnlySpan<byte> head)
  {
    ArgumentNullException.ThrowIfNull(extension);
    string ext = extension.StartsWith('.') ? extension : "." + extension;
    if (BinaryExtensions.Contains(ext))
    {
      return true;
    }

    int probe = Math.Min(head.Length, NullByteProbeBytes);
    for (int i = 0; i < probe; i++)
    {
      if (head[i] == 0)
      {
        return true;
      }
    }

    return false;
  }

  private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
  {
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".gz", ".tar", ".7z",
    ".exe", ".dll", ".so", ".dylib", ".ttf", ".otf", ".woff", ".woff2",
    ".pdf", ".db", ".sqlite",
  };
}