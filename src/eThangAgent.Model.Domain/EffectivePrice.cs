namespace eThangAgent.ModelDomain;

/// <summary>Why an effective input price does - or does not - carry a prompt-cache discount
///     (issue #294, D3). The discount is the single most consequential number in a provider
///     comparison, so the REASON it was or was not applied travels WITH the price instead of
///     being re-derived by every caller that wants to say something about it.</summary>
public enum CacheDiscountBasis
{
  /// <summary>The provider states it caches prompts implicitly, so the advertised cache-read
  ///     price is what a cached input token actually costs.</summary>
  ImplicitCachingSupported,

  /// <summary>The provider states it does NOT cache implicitly. No discount applies, whatever
  ///     cache-read price it advertises (D3). This is the rule that stops a comparison
  ///     recommending a discount that will never be charged.</summary>
  ImplicitCachingUnsupported,

  /// <summary>The catalog never said (a row persisted before the field existed). The discount
  ///     is NOT applied: recommending one that may never be charged is exactly the failure D3
  ///     exists to prevent. The caller can still render the fact as unverified rather than
  ///     claiming the provider does not cache.</summary>
  ImplicitCachingUnverified,

  /// <summary>No price could be computed because a price the arithmetic needs is unknown
  ///     (D2). The caller renders this as unknown, never as zero.</summary>
  PricesUnknown,
}

/// <summary>What one provider's input tokens cost at a STATED hit rate (issue #294, D1), with
///     the cache-READ cost and the cache-WRITE cost named separately (B7).
///     <para>WHY THE WRITE COST IS ITS OWN MEMBER: a cache write is paid once to populate a
///     prefix; a cache read is paid on every later call. Blending them would need a
///     read-to-write ratio, which is a constant this domain refuses to invent (D4's rule
///     applied to the cost formula). <see cref="ReadPerInputToken"/> is the steady-state
///     number a comparison shows; <see cref="IncludingCacheWritePerInputToken"/> is the
///     composed number for a caller that knows the session will pay to write.</para>
///     <para>A null <see cref="ReadPerInputToken"/> means UNKNOWN, never free (D2): a partial
///     price is an understatement presented as a fact, the same lie as an overstatement.</para></summary>
/// <param name="ReadPerInputToken">The blended per-input-token cost of a cached read,
///     <c>(1-h)*prompt + h*cacheRead</c>, or null when a price it needs is unknown.</param>
/// <param name="CacheWritePerInputToken">What the provider charges to WRITE an input token
///     into its cache, or null when it advertises no such price - or when caching is not in
///     play at all, because a provider that does not cache has no cache to write.</param>
/// <param name="HitRate">The hit rate this price was computed at, carried so no caller can
///     show the number without the assumption behind it (D1, parent D2).</param>
/// <param name="DiscountBasis">Why the discount was or was not applied.</param>
public sealed record EffectiveInputPrice(
    decimal? ReadPerInputToken,
    decimal? CacheWritePerInputToken,
    decimal HitRate,
    CacheDiscountBasis DiscountBasis)
{
  /// <summary>True when the read cost could be computed. False means the caller renders
  ///     <c>pricing unknown</c> rather than a number.</summary>
  public bool IsKnown => ReadPerInputToken.HasValue;

  /// <summary>True when the advertised cache-read price is genuinely what a cached token
  ///     costs. False for every other basis - including unverified, where the discount is
  ///     withheld rather than assumed.</summary>
  public bool CacheDiscountApplied => DiscountBasis == CacheDiscountBasis.ImplicitCachingSupported;

  /// <summary>The read cost plus the one-off cache-write cost, for a caller that knows the
  ///     session will pay to write. Null when either part is unknown - a total missing a
  ///     component is not a total.</summary>
  public decimal? IncludingCacheWritePerInputToken
      => ReadPerInputToken is { } read && CacheWritePerInputToken is { } write ? read + write : null;

  /// <summary>The price of a provider whose prices are entirely unknown at this hit rate.
  ///     One shape for every unknown, so a caller never has to special-case null.</summary>
  public static EffectiveInputPrice Unknown(decimal hitRate)
      => new(null, null, hitRate, CacheDiscountBasis.PricesUnknown);
}

/// <summary>The arithmetic that turns catalog prices plus a hit rate into a comparable cost
///     (issue #294, D1), as a PURE function - no clock, no store, no ambient state, so it is
///     unit tested against fixtures and behaves identically for every caller.
///     <para>WHY THIS EXISTS AS ONE FUNCTION: <see cref="UsageCost"/> is the precedent - one
///     formula, one place, so no caller can invent a second version that disagrees. Effective
///     price is the same problem with one difference: it must be computed for provider rows
///     that never ran, which is why the hit rate is an ARGUMENT rather than something read
///     from the ledger. Choosing the hit rate is the caller's job and the caller names it.</para>
///     <para>THE HIT RATE IS NEVER A CONSTANT HERE. A session at 97% and a session at 0% are
///     comparing different providers for the same model, and a formula that picked one for
///     them would be right for one and silently wrong for the other.</para>
///     <para>WHY ONLY THE INPUT PRICE IS BLENDED: prompt caching changes what an INPUT token
///     costs and nothing else, so <see cref="ModelProviderEntry.CompletionPricePerToken"/> is
///     already the effective output price and a surface shows it directly. Blending it in here
///     would need an output-to-input ratio, which is another constant this domain refuses to
///     invent - the same reason the cache write is a separate member rather than folded into
///     the read cost.</para></summary>
public static class EffectivePrice
{
  /// <summary>The effective input price of one provider, from its three advertised prices and
  ///     the hit rate the caller expects.
  ///     <para><paramref name="supportsImplicitCaching"/> is D3's fact and is TRI-STATE on
  ///     purpose, because the catalog's own member is: true applies the advertised discount,
  ///     false withholds it, and null (the catalog never said) also withholds it - the
  ///     conservative direction, since a discount that is never charged is the failure this
  ///     rule exists to prevent.</para></summary>
  /// <param name="promptPrice">The uncached-input price per token, or null when unknown.</param>
  /// <param name="cacheReadPrice">The cache-read price per token, or null when unknown.</param>
  /// <param name="cacheWritePrice">The cache-write price per token, or null when the provider
  ///     advertises none.</param>
  /// <param name="hitRate">The share of input tokens expected to be cache hits, in [0,1].</param>
  /// <param name="supportsImplicitCaching">Whether the provider caches implicitly, or null
  ///     when the catalog never said.</param>
  public static EffectiveInputPrice Input(decimal? promptPrice, decimal? cacheReadPrice,
      decimal? cacheWritePrice, decimal hitRate, bool? supportsImplicitCaching)
  {
    ValidateHitRate(hitRate);
    CacheDiscountBasis basis = supportsImplicitCaching switch
    {
      true => CacheDiscountBasis.ImplicitCachingSupported,
      false => CacheDiscountBasis.ImplicitCachingUnsupported,
      null => CacheDiscountBasis.ImplicitCachingUnverified,
    };

    // D3: without a verified implicit cache the input price IS the prompt price, whatever
    // cache-read price the catalog advertises. The write price goes with it - a provider that
    // does not cache has no cache to write, so charging one would invent a cost.
    if (basis != CacheDiscountBasis.ImplicitCachingSupported)
    {
      return promptPrice is { } promptOnly
          ? new EffectiveInputPrice(promptOnly, null, hitRate, basis)
          : EffectiveInputPrice.Unknown(hitRate);
    }

    decimal? read = ReadPrice(promptPrice, cacheReadPrice, hitRate);
    return read is null
        ? EffectiveInputPrice.Unknown(hitRate)
        : new EffectiveInputPrice(read, cacheWritePrice, hitRate, basis);
  }

  /// <summary>The effective input price of one catalog row at a stated hit rate, resolving
  ///     the prices from the row's own price LADDER for a call of <paramref name="promptTokens"/>
  ///     prompt tokens (issue #293): an endpoint that charges $3/M up to 200,000 prompt tokens
  ///     and $6/M above it is compared at the rung the session will actually be billed from.
  ///     <para>The cache-write price is resolved from the same rung rather than from the base
  ///     row, because a tier that states different numbers is the tier a long call pays.</para></summary>
  public static EffectiveInputPrice For(ModelProviderEntry entry, decimal hitRate, int promptTokens = 0)
  {
    ArgumentNullException.ThrowIfNull(entry);
    ValidateHitRate(hitRate);
    UsagePrices prices = entry.PricesFor(promptTokens);
    PriceOverride? tier = PriceTiers.Applicable(entry.PriceOverrides, promptTokens);
    decimal? cacheWrite = tier is null
        ? entry.PromptCacheWritePricePerToken
        : tier.PromptCacheWritePricePerToken;
    return Input(PriceOrNull(prices.PromptPricePerToken),
        prices.PromptCacheReadPricePerToken is { } cacheRead ? PriceOrNull(cacheRead) : null,
        PriceOrNull(cacheWrite), hitRate, entry.SupportsImplicitCaching);
  }

  /// <summary><c>(1-h)*prompt + h*cacheRead</c>, requiring each price only when its own term is
  ///     non-zero - the same discipline <see cref="UsageCost"/> applies to a ledger row, so a
  ///     session that will see no cache hits is not made unknown by a missing cache-read price.
  ///     </summary>
  private static decimal? ReadPrice(decimal? promptPrice, decimal? cacheReadPrice, decimal hitRate)
  {
    decimal uncachedShare = 1m - hitRate;
    decimal cost = 0m;
    if (uncachedShare > 0m)
    {
      if (promptPrice is not { } prompt)
      {
        return null;
      }

      cost += uncachedShare * prompt;
    }

    if (hitRate > 0m)
    {
      if (cacheReadPrice is not { } cacheRead)
      {
        return null;
      }

      cost += hitRate * cacheRead;
    }

    return cost;
  }

  /// <summary>A negative advertised price (the routing pseudo-models' -1, "set at routing
  ///     time") is not a price; an absent one is unknown. A served zero stays a real price.
  ///     </summary>
  private static decimal? PriceOrNull(decimal? price) => price is { } value && value >= 0m ? value : null;

  /// <summary>A hit rate outside [0,1] is a programmer error, not a domain failure: no
  ///     provider, session, or catalog can produce one, so it is thrown rather than clamped -
  ///     clamping would turn a caller's arithmetic bug into a plausible-looking price.</summary>
  private static void ValidateHitRate(decimal hitRate)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(hitRate);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(hitRate, 1m);
  }
}
