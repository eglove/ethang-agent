namespace eThangAgent.ModelDomain;

/// <summary>One model+provider pair from the OpenRouter catalog: effective prices
/// (as served - the endpoints API's prices are final), per-provider context and max
/// completion tokens, capability scores, advertised latency/throughput, and per-model
/// capabilities. Replaces ModelCatalogEntry.</summary>
/// <para><see cref="PromptCacheReadPricePerToken"/> is NULLABLE on purpose: "no cache
/// read price advertised" is a different fact from "cache reads are free", and the
/// non-nullable prices beside it conflate the two. A negative advertised price (the
/// routing pseudo-models' -1, meaning "set at routing time") is likewise not a price.</para>
public sealed record ModelProviderEntry(
    string ModelId,
    string? ProviderName,
    decimal PromptPricePerToken,
    decimal? PromptCacheReadPricePerToken,
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
