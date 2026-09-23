namespace eThangAgent.SkillDomain;

public sealed record SkillDirectoryLoad(IReadOnlyList<SkillDefinition> Skills, IReadOnlyList<string> Diagnostics);
