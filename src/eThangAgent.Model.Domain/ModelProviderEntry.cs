namespace eThangAgent.ModelDomain;

/// <summary>One model+provider pair from the OpenRouter catalog: effective prices
/// (after discount), per-provider context and max completion tokens, capability scores,
/// advertised latency/throughput, and per-model capabilities. Replaces ModelCatalogEntry.</summary>
public sealed record ModelProviderEntry(
    string ModelId,
    string ProviderName,
    decimal PromptPricePerToken,
    decimal CompletionPricePerToken,
    int ContextLength,
    int MaxCompletionTokens,
    bool SupportsToolUse,
    bool SupportsVision,
    double? IntelligenceScore,
    double? CodingScore,
    double? AgenticScore,
    double? LatencyMs,
    double? ThroughputTokensPerSec,
    string? Description)
{
    /// <summary>Composite key for exclusion: "ModelId:ProviderName".</summary>
    public string Key => $"{ModelId}:{ProviderName}";
}