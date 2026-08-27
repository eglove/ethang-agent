using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public static class SkillMarkdown
{
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
    if (lines.Length < 2 || lines[0].TrimEnd() != "---")
    {
      return Fail(new DomainError("MissingFrontmatter",
          "Skill file must open with a '---' frontmatter fence."));
    }

    string? name = null, description = null;
    int close = -1;
    for (int i = 1; i < lines.Length; i++)
    {
      if (lines[i].TrimEnd() == "---")
      {
        close = i;
        break;
      }
      int idx = lines[i].IndexOf(':', StringComparison.Ordinal);
      if (idx <= 0)
      {
        continue;
      }

      string key = lines[i][..idx].Trim();
      string value = lines[i][(idx + 1)..].Trim();
      switch (key)
      {
        case "name" when name is null: name = value; break;
        case "description" when description is null: description = value; break;
        default:
          break;
      }
    }
    if (close < 0)
    {
      return Fail(new DomainError("MissingFrontmatter",
          "Frontmatter is never closed; expected a second '---' line."));
    }

    if (name is null)
    {
      return Fail(new DomainError("MissingKey", "Frontmatter requires a 'name:' key."));
    }

    if (description is null)
    {
      return Fail(new DomainError("MissingKey", "Frontmatter requires a 'description:' key."));
    }

    if (description.Length == 0)
    {
      return Fail(new DomainError("EmptyDescription", "'description' must be non-empty."));
    }

    string[] bodyLines = lines[(close + 1)..];
    if (bodyLines.Length > 0 && bodyLines[0].Length == 0)
    {
      bodyLines = bodyLines[1..];
    }

    return Result.Success(new ParsedSkill(name, description, string.Join('\n', bodyLines)));
  }

  private static Result<ParsedSkill> Fail(DomainError error) => Result.Failure<ParsedSkill>(error);
}
