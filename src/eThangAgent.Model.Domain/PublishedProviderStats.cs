namespace eThangAgent.ModelDomain;

/// <summary>What a provider publishes about one endpoint's real traffic (issue #296
///     follow-up): the cache-hit share it observes and the prices it actually collects.
///     <para>WHY THIS EXISTS: a provider comparison has to project a price at SOME hit rate,
///     and a session that has never called a model+provider pair has no measurement of its
///     own to project at. The documented default is a poor stand-in when the provider itself
///     publishes the figure for that exact endpoint - measured live 2026-10-27, one model's
///     seven endpoints ranged from 0% to 74.5% cache-hit rate, so a single global rate is
///     wrong for nearly every row on the list.</para>
///     <para>EVERY MEMBER IS NULLABLE AND NULL MEANS 'PUBLISHED NOTHING'. A provider that
///     reports no rate is not a provider with a 0% rate, and coercing the two together would
///     rank a heavily-caching provider as though it never cached. The same rule the catalog's
///     prices follow (issue #289).</para>
///     <para>THE PUBLISHED PRICES ARE CARRIED BUT NOT SUBSTITUTED for our computed figures:
///     they are what OpenRouter's whole traffic paid, which is a different question from what
///     a call at OUR prompt size and the row's own price-ladder rung would cost. They are
///     here so a surface can show the comparison, never so a formula can quietly swap its
///     input.</para></summary>
/// <param name="CacheHitRate">The share of prompt tokens the provider served from cache, in
///     [0,1], or null when it published none.</param>
/// <param name="EffectiveInputPricePerToken">The observed per-input-token price actually
///     collected, cache discounts included, or null when unpublished.</param>
/// <param name="EffectiveOutputPricePerToken">The observed per-output-token price actually
///     collected, or null when unpublished.</param>
/// <param name="TotalTokens">The token volume the figures rest on, or null when unpublished.
///     A rate measured over a thousand tokens and one measured over ten billion are different
///     qualities of evidence, and a surface that showed them alike would present a coincidence
///     as a fact.</param>
public sealed record PublishedProviderStats(
    decimal? CacheHitRate,
    decimal? EffectiveInputPricePerToken,
    decimal? EffectiveOutputPricePerToken,
    long? TotalTokens)
{
  /// <summary>True when the provider published a cache-hit rate for this endpoint, which is
  ///     the one figure the price projection consumes.</summary>
  public bool HasCacheHitRate => CacheHitRate.HasValue;

  /// <summary>This endpoint's published rate as an assumption, or null when it published
  ///     none. The caller falls through to the documented default rather than inventing a
  ///     number the provider never stated.</summary>
  public HitRateAssumption? Assumption => CacheHitRate is { } rate
      ? HitRateAssumption.FromPublished(rate)
      : null;

  /// <summary>Builds a record after validating the published rate: a provider cannot
  ///     publish a negative share or one above 100%, and a value outside that range means the
  ///     parser read the wrong field - thrown rather than clamped, so a bug cannot become a
  ///     plausible-looking price. The parser routes every value through here.</summary>
  public static PublishedProviderStats Create(decimal? cacheHitRate,
      decimal? effectiveInputPricePerToken, decimal? effectiveOutputPricePerToken,
      long? totalTokens)
  {
    if (cacheHitRate is { } rate)
    {
      ArgumentOutOfRangeException.ThrowIfNegative(rate);
      ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1m);
    }

    return new PublishedProviderStats(cacheHitRate, effectiveInputPricePerToken,
        effectiveOutputPricePerToken, totalTokens);
  }
}
