using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public static class SkillMarkdown
{
  private const string Fence = "---";

  /// <summary>Parses a skill markdown file: '---' fenced frontmatter with required
  ///     <c>name:</c> and non-empty <c>description:</c> keys, then the body.</summary>
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

    (string? name, string? description, int close) = ScanFrontmatter(lines);
    DomainError? invalid = ValidateFrontmatter(name, description, close);
    if (invalid is not null)
    {
      return Fail(invalid);
    }

    string[] bodyLines = lines[(close + 1)..];
    if (bodyLines.Length > 0 && bodyLines[0].Length == 0)
    {
      bodyLines = bodyLines[1..];
    }

    ParsedSkill skill = new(name!, description!, string.Join('\n', bodyLines));
    return Result.Success(skill);
  }

  /// <summary>Walks the frontmatter block, harvesting the first occurrence of each
  ///     required key and the closing fence index (-1 when never closed).</summary>
  private static (string? Name, string? Description, int CloseIndex) ScanFrontmatter(string[] lines)
  {
    string? name = null, description = null;
    int close = -1;
    for (int i = 1; i < lines.Length; i++)
    {
      if (lines[i].TrimEnd() == Fence)
      {
        close = i;
        break;
      }

      int idx = lines[i].IndexOf(':', StringComparison.Ordinal);
      if (idx <= 0)
      {
        continue;
      }

      (name, description) = HarvestKey(lines[i], idx, name, description);
    }

    return (name, description, close);
  }

  private static (string? Name, string? Description) HarvestKey(
      string line, int idx, string? name, string? description)
  {
    string key = line[..idx].Trim();
    string value = line[(idx + 1)..].Trim();
    switch (key)
    {
      case "name" when name is null: name = value; break;
      case "description" when description is null: description = value; break;
      default:
        break;
    }

    return (name, description);
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
