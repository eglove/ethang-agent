namespace eThangAgent.SkillDomain;

/// <summary>A methodology skill: built-ins ship verbatim with the app;
/// learned skills are created by the agent itself (provenance-tracked).</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Body,
    int Version,
    SkillSource Source,
    string? ProvenanceSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
