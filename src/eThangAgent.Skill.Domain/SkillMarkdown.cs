using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public static class SkillMarkdown
{
  private const string Fence = "---";

  /// <summary>Parses a skill markdown file: '---' fenced frontmatter with required
  ///     <c>name:</c> and non-empty <c>description:</c> keys, then the body. The
  ///     agentskills.io subset (<c>disable-model-invocation</c>, <c>metadata.version</c>)
  ///     is harvested; known-but-unapplied and unknown keys yield non-fatal warnings.</summary>
  public static Result<ParsedSkill> Parse(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.StartsWith('\uFEFF'))
    {
      text = text[1..];
    }

    string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    if (lines.Length < 2 || lines[0].TrimEnd() != Fence)
    {
      return Fail(new DomainError("MissingFrontmatter",
          "Skill file must open with a '---' frontmatter fence."));
    }

    FrontmatterScan scan = ScanFrontmatter(lines);
    DomainError? invalid = ValidateFrontmatter(scan.Name, scan.Description, scan.CloseIndex);
    if (invalid is not null)
    {
      return Fail(invalid);
    }

    string[] bodyLines = lines[(scan.CloseIndex + 1)..];
    if (bodyLines.Length > 0 && bodyLines[0].Length == 0)
    {
      bodyLines = bodyLines[1..];
    }

    ParsedSkill skill = new(scan.Name!, scan.Description!, string.Join('\n', bodyLines),
        scan.Manual, scan.Version, scan.Warnings);
    return Result.Success(skill);
  }

  private sealed record FrontmatterScan(
      string? Name,
      string? Description,
      bool Manual,
      int? Version,
      IReadOnlyList<string> Warnings,
      int CloseIndex);

  /// <summary>Walks the frontmatter block in a single pass, harvesting the first
  ///     occurrence of each required key, the manual-invocation flag, and the
  ///     metadata version, collecting warnings, and finding the closing fence
  ///     index (-1 when never closed).</summary>
  private static FrontmatterScan ScanFrontmatter(string[] lines)
  {
    FrontmatterScanner scanner = new(lines);
    return scanner.Scan();
  }

  // Mutable scan state for one frontmatter block; the walk splits across small
  // methods over these fields.
  private sealed class FrontmatterScanner(string[] lines)
  {
    private readonly string[] _lines = lines;
    private string? _name;
    private string? _description;
    private bool _manual;
    private bool _manualSeen;
    private int? _version;
    private bool _versionSeen;
    private bool _inMetadataBlock;
    private readonly HashSet<string> _warningsSeen = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private int _close = -1;

    internal FrontmatterScan Scan()
    {
      for (int i = 1; i < _lines.Length; i++)
      {
        if (_lines[i].TrimEnd() == Fence)
        {
          _close = i;
          break;
        }

        HarvestLine(_lines[i]);
      }

      return new FrontmatterScan(_name, _description, _manual, _version, _warnings, _close);
    }

    private void HarvestLine(string line)
    {
      if (_inMetadataBlock && IsIndented(line))
      {
        HarvestMetadataSubkey(line);
        return;
      }

      if (line.Length > 0)
      {
        _inMetadataBlock = false; // blank lines never end the block; any unindented content does
      }

      int idx = line.IndexOf(':', StringComparison.Ordinal);
      if (idx <= 0)
      {
        return;
      }

      HarvestKey(line[..idx].Trim(), line[(idx + 1)..].Trim());
    }

    private void HarvestKey(string key, string value)
    {
      switch (key)
      {
        case "name" when _name is null: _name = StripMatchingQuotes(value); break;
        case "name": break; // repeat: first occurrence wins (existing behavior)
        case "description" when _description is null: _description = StripMatchingQuotes(value); break;
        case "description": break; // repeat: first occurrence wins (existing behavior)
        case "metadata": _inMetadataBlock = true; break;
        default: HarvestOtherKey(key, value); break;
      }
    }

    private void HarvestOtherKey(string key, string value)
    {
      switch (key)
      {
        case "disable-model-invocation" when !_manualSeen:
          _manualSeen = true;
          HarvestManualFlag(value);
          break;
        case "disable-model-invocation": break; // repeat: first occurrence wins
        case "allowed-tools" or "license" or "compatibility" when !_warningsSeen.Contains(key):
          RecordKeyWarning(key);
          break;
        default:
          if (!_warningsSeen.Contains(key))
          {
            RecordKeyWarning(key);
          }

          break; // a repeated key of either class was already warned about once
      }
    }

    private void HarvestManualFlag(string value)
    {
      if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
      {
        _manual = true;
      }
      else if (!value.Equals("false", StringComparison.OrdinalIgnoreCase))
      {
        _warnings.Add("unknown value for disable-model-invocation");
      }
    }

    /// <summary>Records a frontmatter warning once per skill: each known-but-unapplied
    ///     and each unknown key warns on its FIRST occurrence only (the follow-up
    ///     cosmetics ruling - repeated lines are one fact, not several).</summary>
    private void RecordKeyWarning(string key)
    {
      _ = _warningsSeen.Add(key);
      bool known = key is "allowed-tools" or "license" or "compatibility";
      _warnings.Add(known
          ? $"ignored key {key} (known but unapplied)"
          : $"unknown frontmatter key {key}");
    }

    /// <summary>Inside a <c>metadata:</c> block only <c>version:</c> is honored; every
    ///     other subkey is the format's free-form map and is ignored silently.</summary>
    private void HarvestMetadataSubkey(string line)
    {
      int idx = line.IndexOf(':', StringComparison.Ordinal);
      if (idx <= 0)
      {
        return;
      }

      string key = line[..idx].Trim();
      if (key != "version" || _versionSeen)
      {
        return;
      }

      _versionSeen = true;
      string value = line[(idx + 1)..].Trim();
      if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
      {
        _version = parsed;
      }
      else
      {
        _warnings.Add("metadata.version is not an integer");
      }
    }

    private static bool IsIndented(string line) =>
        line.StartsWith(' ') || line.StartsWith('\t');

    /// <summary>Strips ONE matching pair of single or double quotes; unmatched or
    ///     partial quoting is left verbatim.</summary>
    private static string StripMatchingQuotes(string value)
    {
      bool quoted = value.Length >= 2
          && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'));
      return quoted ? value[1..^1] : value;
    }
  }

  /// <summary>Required-key rules, in the documented order: closed fence, name,
  ///     description, non-empty description.</summary>
  private static DomainError? ValidateFrontmatter(string? name, string? description, int close)
  {
    if (close < 0)
    {
      return new DomainError("MissingFrontmatter",
          "Frontmatter is never closed; expected a second '---' line.");
    }

    if (name is null)
    {
      return new DomainError("MissingKey", "Frontmatter requires a 'name:' key.");
    }

    if (description is null)
    {
      return new DomainError("MissingKey", "Frontmatter requires a 'description:' key.");
    }

    DomainError? empty = description.Length == 0
      ? new DomainError("EmptyDescription", "'description' must be non-empty.")
      : null;
    return empty;
  }

  private static Result<ParsedSkill> Fail(DomainError error) => Result.Failure<ParsedSkill>(error);
}
