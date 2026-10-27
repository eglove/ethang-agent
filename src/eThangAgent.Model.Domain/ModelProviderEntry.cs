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
/// <para>THE PERFORMANCE FIELDS ARE KEPT AND DISTRUSTED (issue #293, D1).
/// <see cref="LatencyMs"/> and <see cref="ThroughputTokensPerSec"/> stay on the record and
/// stay nullable: the provider may populate them later, and removing them would be a lie in
/// the other direction. But NO CONSUMER TREATS THEM AS A LATENCY SOURCE, because measured
/// live on 2026-10-27 across 5 models and 52 endpoints they were null on EVERY row - the
/// fields are structurally dead, and a picker that rendered them would show a blank where a
/// comparison belongs. Measured call durations from the usage ledger are the honest latency
/// source; <see cref="ProviderPerformanceText"/> is the one place that decides how an absent
/// number is stated, so no surface can quietly render it as zero.</para>
/// <para><c>UptimeLast30m</c>/<c>UptimeLast5m</c>/<c>UptimeLast1d</c> are the endpoint's
/// served-request success rates as the API reports them (0-100), each null when it reported
/// none (issue #293). THIS is the reliability signal that is actually populated - measured
/// live, 35 of 35 endpoints on one model carried it - and a provider at 87% is a real risk to
/// a long session in a way no price column can express.</para>
/// <para><c>SupportsImplicitCaching</c> is whether the endpoint caches prompts implicitly, or
/// null when the field was never parsed (issue #293). TRI-STATE ON PURPOSE: a row loaded from
/// a catalog cache written before this member existed has NOT said false, it has said nothing,
/// and coercing that to false would tell the user a caching provider does not cache. False is
/// only ever a parsed false.</para>
/// <para><c>PromptCacheWritePricePerToken</c> is what the endpoint charges to WRITE a prompt
/// into its cache, or null when it advertises no such price (issue #293). It completes the
/// cost formula: a model whose cache READS are free but whose cache WRITES cost 1.25x the
/// prompt price is not the bargain the read price alone suggests.</para>
/// <para><c>PriceOverrides</c> is the endpoint's price ladder, or null/empty when it serves a
/// single price (issue #293). Each rung applies once a call's prompt reaches its
/// <see cref="PriceOverride.MinPromptTokens"/>; <see cref="PricesFor"/> resolves which one a
/// call is billed from.</para>
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
    bool IsRouter = false,
    double? UptimeLast30m = null,
    double? UptimeLast5m = null,
    double? UptimeLast1d = null,
    bool? SupportsImplicitCaching = null,
    decimal? PromptCacheWritePricePerToken = null,
    IReadOnlyList<PriceOverride>? PriceOverrides = null)
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

  /// <summary>True when this endpoint serves a price LADDER rather than one price (issue
  ///     #293): a call's cost then depends on its prompt size, which a single base price
  ///     cannot express.</summary>
  public bool HasPriceOverrides => PriceOverrides is { Count: > 0 };

  /// <summary>The prices a call with <paramref name="promptTokens"/> prompt tokens is billed
  ///     at (issue #293): the applicable ladder rung when the endpoint serves one, else this
  ///     entry's base prices. A rung that omits a price leaves that member null - the tier
  ///     said nothing about it, and a base price is not inherited into a tier that exists to
  ///     state different numbers.</summary>
  public UsagePrices PricesFor(int promptTokens)
  {
    PriceOverride? tier = PriceTiers.Applicable(PriceOverrides, promptTokens);
    return tier is null
        ? new UsagePrices(PromptPricePerToken, PromptCacheReadPricePerToken, CompletionPricePerToken)
        : new UsagePrices(tier.PromptPricePerToken, tier.PromptCacheReadPricePerToken,
            tier.CompletionPricePerToken);
  }
}
