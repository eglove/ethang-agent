namespace eThangAgent.SkillDomain;

/// <summary>A parsed skill file: frontmatter name/description plus the body below the fence.</summary>
public sealed record ParsedSkill(string Name, string Description, string Body);
