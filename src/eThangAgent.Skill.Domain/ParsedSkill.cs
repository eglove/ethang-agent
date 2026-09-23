namespace eThangAgent.SkillDomain;

/// <summary>A parsed skill file: frontmatter name/description plus the body below the fence,
/// the manual-invocation flag, an optional metadata version, and non-fatal frontmatter
/// warnings (warnings never fail the parse).</summary>
public sealed record ParsedSkill(
    string Name,
    string Description,
    string Body,
    bool Manual,
    int? Version,
    IReadOnlyList<string> Warnings);
