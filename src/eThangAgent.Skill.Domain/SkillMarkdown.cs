using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

public static class SkillMarkdown
{
    public sealed record ParsedSkill(string Name, string Description, string Body);

    public static Result<ParsedSkill> Parse(string text)
    {
        if (text.StartsWith("\uFEFF", StringComparison.Ordinal)) text = text[1..];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd() != "---")
            return Fail(new Error("MissingFrontmatter",
                "Skill file must open with a '---' frontmatter fence."));

        string? name = null, description = null;
        int close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { close = i; break; }
            var idx = lines[i].IndexOf(':');
            if (idx <= 0) continue;
            var key = lines[i][..idx].Trim();
            var value = lines[i][(idx + 1)..].Trim();
            switch (key)
            {
                case "name" when name is null: name = value; break;
                case "description" when description is null: description = value; break;
            }
        }
        if (close < 0)
            return Fail(new Error("MissingFrontmatter",
                "Frontmatter is never closed; expected a second '---' line."));
        if (name is null) return Fail(new Error("MissingKey", "Frontmatter requires a 'name:' key."));
        if (description is null) return Fail(new Error("MissingKey", "Frontmatter requires a 'description:' key."));
        if (description.Length == 0) return Fail(new Error("EmptyDescription", "'description' must be non-empty."));

        var bodyLines = lines[(close + 1)..];
        if (bodyLines.Length > 0 && bodyLines[0].Length == 0) bodyLines = bodyLines[1..];
        return Result<ParsedSkill>.Success(new ParsedSkill(name, description, string.Join('\n', bodyLines)));
    }

    private static Result<ParsedSkill> Fail(Error error) => Result<ParsedSkill>.Failure(error);
}
