namespace eThangAgent.SkillDomain;

/// <summary>The announcement view of one hot reload pass: skills visible after the
/// reload that were absent before (Added), present before and absent after (Removed),
/// and present in both with a source-label or content change (Changed). Manual skills
/// never appear in the listing, so a manual flip surfaces as a removal (or, flipped
/// off, an addition) - the announcement view, not a file-diff.</summary>
public sealed record SkillReloadDiff(
    IReadOnlyList<SkillDefinition> Added,
    IReadOnlyList<SkillDefinition> Removed,
    IReadOnlyList<SkillDefinition> Changed);
