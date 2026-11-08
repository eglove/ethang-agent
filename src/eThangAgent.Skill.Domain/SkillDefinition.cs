namespace eThangAgent.SkillDomain;

/// <summary>A methodology skill: built-ins ship as embedded resources in the Skill
/// Domain assembly; file skills are read from the configured skill directories.</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Body,
    int Version,
    SkillSource Source,
    bool Manual = false,
    string? Origin = null);
