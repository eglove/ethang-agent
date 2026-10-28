namespace eThangAgent.ModelDomain;

/// <summary>The endpoint order a Cache Floor request carries, computed as a PURE function over
///     catalog rows - no clock, no store, no network, so it is unit tested against fixtures and
///     behaves identically for every caller.
///     <para>WHY THIS EXISTS: the provider offers no sort dimension for cache-read price. Its
///     <c>provider.sort</c> enum is closed to price/throughput/latency/exacto and rejects
///     anything else with HTTP 400 (verified 2026-10-28), so the only way to express "prefer the
///     cheapest cache reads" is an explicit <c>provider.order</c> list. This function computes
///     that list.</para>
///     <para>WHY CACHE-READ PRICE AND NOT THE BLENDED PRICE: <c>:floor</c> already sorts by list
///     price, and on the endpoints measured 2026-10-28 those two orders disagree on 28 of 165
///     multi-endpoint models - the cache-read pick averaging 1.61x the input price. A session
///     whose prompts are cached heavily wants the former; this variant states that intent
///     without asking the user to reason about hit rates.</para>
///     <para>WHY IT DOES NOT USE ProviderRanking: that type ranks on a BLENDED price computed at
///     a caller-supplied hit rate. A cache-read sort consults neither the hit rate nor the
///     blend, so reusing it would force an irrelevant argument into the signature and compute a
///     price nobody reads. The two answer different questions and each has one implementation.
///     </para>
///     <para>THE ORDER IS A PREFERENCE, NOT A PIN. The request sends <c>allow_fallbacks: true</c>,
///     so an unavailable listed endpoint falls through to the rest of the pool rather than
///     failing the turn. Measured: <c>provider.order</c> with a flex slug first served that
///     endpoint on one run and another listed endpoint on the next, so the list is a head, not
///     a guarantee.</para></summary>
public static class CacheFloorOrder
{
  /// <summary>How many endpoints a request names. A model can carry 36; sending all of them
  ///     bloats every request for no benefit, because fallbacks cover the rest of the pool.</summary>
  public const int MaxOrderedEndpoints = 8;

  /// <summary>The endpoint slugs, cheapest cache-read price first. Empty when no row carries
  ///     both an endpoint tag and a non-negative cache-read price - the caller then sends no
  ///     provider block at all rather than an empty or fabricated order.</summary>
  /// <param name="endpoints">The catalog rows for ONE model.</param>
  /// <param name="maxEndpoints">How many slugs to name, capped by the pool size.</param>
  public static IReadOnlyList<string> Slugs(IReadOnlyList<ModelProviderEntry> endpoints,
      int maxEndpoints = MaxOrderedEndpoints)
  {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEndpoints);

    // UNKNOWN IS NEVER CHEAPEST. A row advertising no cache-read price, or a negative one (the
    // routing pseudo-models' -1, "set at routing time"), is not a candidate for cheapest and
    // sorts LAST. The default comparer would float a null key to the TOP ascending, so the
    // unknown rank is an explicit leading key rather than a reliance on null ordering. A served
    // zero stays a real price and sorts first.
    //
    // A ROW WITH NO ENDPOINT TAG IS DROPPED, and the filter is on the tag rather than on
    // RoutingSlug: RoutingSlug falls back to the provider NAME when the payload carries no tag,
    // so filtering on it would admit a row whose price describes no distinct endpoint. The order
    // exists to name endpoints whose cache-read price was actually advertised; a name-only row
    // would put a provider in the list whose served endpoint the order never priced.
    return [.. endpoints
        .Where(entry => entry.Variant.Slug is { Length: > 0 })
        .Select(entry => (Entry: entry, CacheRead: CacheReadPrice(entry)))
        .OrderBy(row => row.CacheRead is null ? 1 : 0)
        .ThenBy(row => row.CacheRead)
        .ThenBy(row => row.Entry.PromptPricePerToken is { } prompt && prompt >= 0m
            ? prompt
            : decimal.MaxValue)
        .ThenBy(row => row.Entry.Variant.Slug, StringComparer.Ordinal)
        .Take(maxEndpoints)
        .Select(row => row.Entry.Variant.Slug!)];
  }

  /// <summary>The endpoint's cache-read price, or null when it advertised none. A negative
  ///     value is the routing pseudo-models' "set at routing time" marker, not a price; a
  ///     served zero stays a real price. Null means UNKNOWN, and unknown sorts last - which is
  ///     what keeps "nobody advertised a price" from ranking as "cheapest".</summary>
  private static decimal? CacheReadPrice(ModelProviderEntry entry)
      => entry.PromptCacheReadPricePerToken is { } price && price >= 0m ? price : null;
}
