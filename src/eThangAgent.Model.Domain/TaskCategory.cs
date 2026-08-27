namespace eThangAgent.ModelDomain;

/// <summary>Structured categorization of an agent task: tags, complexity, and capability requirements.</summary>
public sealed record TaskCategory(
    IReadOnlyList<string> Tags,
    int Complexity,
    bool RequiresVision,
    bool RequiresToolUse,
    int? MinContextWindow,
    string? Reasoning);
