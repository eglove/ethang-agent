namespace eThangAgent.ModelDomain;

/// <summary>Filter criteria decided by the LLM in Stage 2 to narrow the model catalog before final selection.</summary>
public sealed record ModelFilter(
    decimal? MaxPromptPricePerToken,
    decimal? MaxCompletionPricePerToken,
    int? MinContextLength,
    bool? RequireToolUse,
    bool? RequireVision,
    double? MinQualityScore);
