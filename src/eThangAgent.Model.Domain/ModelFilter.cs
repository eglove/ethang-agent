namespace eThangAgent.ModelDomain;

/// <summary>Filter criteria decided by the LLM in Stage 2 to narrow the model+provider
/// catalog before final selection. All fields nullable — null means no constraint.</summary>
public sealed record ModelFilter(
    decimal? MaxPromptPricePerToken,
    decimal? MaxCompletionPricePerToken,
    int? MinContextLength,
    int? MaxCompletionTokens,
    bool? RequireToolUse,
    bool? RequireVision,
    double? MinIntelligenceScore,
    double? MinCodingScore,
    double? MinAgenticScore,
    double? MaxLatencyMs,
    double? MinThroughputTokensPerSec);
