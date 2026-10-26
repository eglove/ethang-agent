namespace eThangAgent.ModelDomain;

/// <summary>One model+provider pair from the OpenRouter catalog: effective prices
/// (as served - the endpoints API's prices are final), per-provider context and max
/// completion tokens, capability scores, advertised latency/throughput, and per-model
/// capabilities. Replaces ModelCatalogEntry.</summary>
/// <para>ALL THREE prices are NULLABLE on purpose (issue #289): "no price advertised" is
/// a different fact from "this model is free", and non-nullable prices destroyed the
/// distinction before any consumer could see it - the parser returned 0 for a missing
/// pricing block, so an unpriced model was indistinguishable from a free one. A null
/// price means UNKNOWN (never free); a served "0" is a real price of zero. A negative
/// advertised price (the routing pseudo-models' -1, meaning "set at routing time") is
/// likewise not a price, and stays negative here so a display surface can say so.</para>
/// <para><see cref="SupportsTextOutput"/> is parsed from the architecture's
/// <c>output_modalities</c>. Without it "produces text" was not expressible, so a caller
/// could not tell a chat model from a music generator.</para>
public sealed record ModelProviderEntry(
    string ModelId,
    string? ProviderName,
    decimal? PromptPricePerToken,
    decimal? PromptCacheReadPricePerToken,
    decimal? CompletionPricePerToken,
    int ContextLength,
    int MaxCompletionTokens,
    bool SupportsToolUse,
    bool SupportsVision,
    bool SupportsTextOutput,
    double? IntelligenceScore,
    double? CodingScore,
    double? AgenticScore,
    double? LatencyMs,
    double? ThroughputTokensPerSec,
    string? Description,
    bool IsRouter = false)
{
  /// <summary>Composite key for exclusion: "ModelId:ProviderName".</summary>
  public string Key => $"{ModelId}:{ProviderName}";

  /// <summary>True when both billable prices are PRESENT and zero: the catalog says this
  ///     model costs nothing right now. A model with an absent (null) price is NOT free -
  ///     unknown pricing is the opposite of a known price of zero.</summary>
  public bool IsAdvertisedFree => PromptPricePerToken is 0m && CompletionPricePerToken is 0m;

  /// <summary>True when both billable prices are advertised (present and not negative).
  ///     The routing pseudo-models advertise -1, "set at routing time", which is not a
  ///     price; an absent price advertises nothing.</summary>
  public bool HasAdvertisedPrices
      => PromptPricePerToken is >= 0m && CompletionPricePerToken is >= 0m;
}
