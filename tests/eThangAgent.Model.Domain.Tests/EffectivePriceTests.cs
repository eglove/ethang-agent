namespace eThangAgent.ModelDomain.Tests;

// B1-B3, B7 (issue #294): the arithmetic that turns catalog prices plus a STATED hit rate
// into a comparable cost. The live shape this is measured against is the parent issue's own
// table: on deepseek/deepseek-v4.1-flash-1017 the prompt price runs $0.04/M (Reka) to
// $0.33/M (Alibaba) while the cache-read discount runs 25% off to 98% off, so the cheapest
// prompt price belongs to the WORST provider at this workspace's 96.84% hit rate. A formula
// that ranked on the prompt price picked Reka; this one picks DeepInfra.
public class EffectivePriceTests
{
  // Per-token prices at the magnitudes the catalog actually serves ($0.04/M = 0.00000004).
  private const decimal RekaPrompt = 0.00000004m;    // $0.04/M - the cheapest prompt price
  private const decimal RekaCacheRead = 0.00000003m; // 25% off
  private const decimal DeepInfraPrompt = 0.00000008m;    // $0.08/M - double Reka's
  private const decimal DeepInfraCacheRead = 0.0000000016m; // 98% off

  [Fact]
  public void Input_BlendsPromptAndCacheReadAtTheStatedHitRate()
  {
    // B1: (1 - 0.75) * 0.00000004 + 0.75 * 0.00000001 = 0.00000001 + 0.0000000075.
    EffectiveInputPrice price = EffectivePrice.Input(0.00000004m, 0.00000001m, null, 0.75m,
        supportsImplicitCaching: true);

    Assert.Equal(0.0000000175m, price.ReadPerInputToken);
    Assert.Equal(0.75m, price.HitRate);
    Assert.True(price.IsKnown);
    Assert.True(price.CacheDiscountApplied);
  }

  [Fact]
  public void Input_AtFullHitRate_IsTheCacheReadPriceAlone()
  {
    // The boundary: a session that hits cache on every input token pays only the cache price.
    EffectiveInputPrice price = EffectivePrice.Input(0.00000004m, 0.00000001m, null, 1m,
        supportsImplicitCaching: true);

    Assert.Equal(0.00000001m, price.ReadPerInputToken);
  }

  [Fact]
  public void Input_AtZeroHitRate_IsThePromptPriceAlone()
  {
    // The other boundary, and it is what makes a missing cache-read price harmless here: a
    // session that will see no cache hits needs no cache price to be priced.
    EffectiveInputPrice price = EffectivePrice.Input(0.00000004m, null, null, 0m,
        supportsImplicitCaching: true);

    Assert.Equal(0.00000004m, price.ReadPerInputToken);
  }

  [Fact]
  public void Input_NullCacheReadPrice_IsUnknown_NotThePromptPrice()
  {
    // B2 / D2: the catalog advertises no cache-read price. Charging the cached share at the
    // prompt price would be the old overestimate; charging it at zero would be an
    // understatement presented as a fact. The result is UNKNOWN and the caller renders
    // 'pricing unknown'.
    EffectiveInputPrice price = EffectivePrice.Input(0.00000004m, null, null, 0.9684m,
        supportsImplicitCaching: true);

    Assert.Null(price.ReadPerInputToken);
    Assert.False(price.IsKnown);
    Assert.Equal(CacheDiscountBasis.PricesUnknown, price.DiscountBasis);
  }

  [Fact]
  public void Input_NullPromptPrice_IsUnknown()
  {
    // An unpriced model is not a free one (issue #289); its effective price is unknown too.
    EffectiveInputPrice price = EffectivePrice.Input(null, 0.00000001m, null, 0.5m,
        supportsImplicitCaching: true);

    Assert.Null(price.ReadPerInputToken);
  }

  [Fact]
  public void Input_ZeroPrices_AreRealPricesAndCostZero()
  {
    // A free model's zero is a measurement, not an absence: it must not read as unknown.
    EffectiveInputPrice price = EffectivePrice.Input(0m, 0m, null, 0.5m,
        supportsImplicitCaching: true);

    Assert.Equal(0m, price.ReadPerInputToken);
    Assert.True(price.IsKnown);
  }

  [Fact]
  public void Input_ImplicitCachingUnsupported_IgnoresTheAdvertisedCacheDiscount()
  {
    // B3 / D3 - THE MOST IMPORTANT RULE IN THE FEATURE. The provider advertises a cache-read
    // price 90% below its prompt price but states it does not cache implicitly, so that
    // discount will never be charged. Ranking on it would recommend a price the user cannot
    // get.
    EffectiveInputPrice price = EffectivePrice.Input(0.0000001m, 0.00000001m, null, 0.9684m,
        supportsImplicitCaching: false);

    Assert.Equal(0.0000001m, price.ReadPerInputToken);
    Assert.False(price.CacheDiscountApplied);
    Assert.Equal(CacheDiscountBasis.ImplicitCachingUnsupported, price.DiscountBasis);
  }

  [Fact]
  public void Input_ImplicitCachingUnsupported_ChargesNoCacheWrite()
  {
    // A provider with no implicit cache has no cache to write, so a write price it happens to
    // advertise is not a cost the session will pay.
    EffectiveInputPrice price = EffectivePrice.Input(0.0000001m, 0.00000001m, 0.000000125m,
        0.9684m, supportsImplicitCaching: false);

    Assert.Null(price.CacheWritePerInputToken);
    Assert.Null(price.IncludingCacheWritePerInputToken);
  }

  [Fact]
  public void Input_ImplicitCachingUnverified_WithholdsTheDiscount_ButSaysSo()
  {
    // A row persisted before the catalog parsed the field has said NOTHING - not false. The
    // discount is withheld (recommending one that may never be charged is the failure D3
    // prevents) but the basis says unverified rather than claiming the provider does not
    // cache, so a surface can render it honestly.
    EffectiveInputPrice price = EffectivePrice.Input(0.0000001m, 0.00000001m, null, 0.9684m,
        supportsImplicitCaching: null);

    Assert.Equal(0.0000001m, price.ReadPerInputToken);
    Assert.Equal(CacheDiscountBasis.ImplicitCachingUnverified, price.DiscountBasis);
    Assert.False(price.CacheDiscountApplied);
  }

  [Fact]
  public void Input_CacheWritePrice_IsNamedSeparatelyFromTheReadCost()
  {
    // B7: a cache write is paid once to populate a prefix; a cache read is paid on every
    // later call. The read cost stays the steady-state number and the write is its own member,
    // so a caller that knows the session will pay to write can compose them and one that does
    // not is not silently charged.
    EffectiveInputPrice price = EffectivePrice.Input(0.0000001m, 0.00000001m, 0.000000125m,
        0.5m, supportsImplicitCaching: true);

    Assert.Equal(0.000000055m, price.ReadPerInputToken);
    Assert.Equal(0.000000125m, price.CacheWritePerInputToken);
    Assert.Equal(0.00000018m, price.IncludingCacheWritePerInputToken);
  }

  [Fact]
  public void IncludingCacheWrite_WithoutAWritePrice_IsNull_NotTheReadCost()
  {
    // A total missing a component is not a total: reporting the read cost as the including-
    // write figure would understate the session's first call.
    EffectiveInputPrice price = EffectivePrice.Input(0.0000001m, 0.00000001m, null, 0.5m,
        supportsImplicitCaching: true);

    Assert.Null(price.IncludingCacheWritePerInputToken);
  }

  [Fact]
  public void Input_HitRateOutsideZeroToOne_Throws()
  {
    // A hit rate outside [0,1] is a caller's arithmetic bug, not a domain outcome. Clamping
    // would turn it into a plausible-looking price.
    _ = Assert.Throws<ArgumentOutOfRangeException>(()
        => EffectivePrice.Input(0.0000001m, 0.00000001m, null, 1.5m, true));
    _ = Assert.Throws<ArgumentOutOfRangeException>(()
        => EffectivePrice.Input(0.0000001m, 0.00000001m, null, -0.1m, true));
  }

  [Fact]
  public void For_TheParentsOwnComparison_CheapestPromptPriceLosesAtThisHitRate()
  {
    // THE MEASURED CASE THE WHOLE FEATURE EXISTS FOR (parent #291, 2026-10-27): at this
    // workspace's 96.84% hit rate, DeepInfra at DOUBLE Reka's prompt price is the cheaper
    // provider by a wide margin. The old picker ranked on the prompt price and chose Reka.
    const decimal hitRate = 0.9684m;
    EffectiveInputPrice reka = EffectivePrice.For(
        Entry("Reka", RekaPrompt, RekaCacheRead), hitRate);
    EffectiveInputPrice deepInfra = EffectivePrice.For(
        Entry("DeepInfra", DeepInfraPrompt, DeepInfraCacheRead), hitRate);

    _ = Assert.NotNull(reka.ReadPerInputToken);
    _ = Assert.NotNull(deepInfra.ReadPerInputToken);
    Assert.True(deepInfra.ReadPerInputToken < reka.ReadPerInputToken,
        $"DeepInfra {deepInfra.ReadPerInputToken} should beat Reka {reka.ReadPerInputToken}");
  }

  [Fact]
  public void For_LongPrompt_ResolvesTheUpperLadderRung()
  {
    // Issue #293's ladder: a 300,000-token call on an endpoint charging $3/M up to 200,000
    // and $6/M above it is compared at the rung it will actually be billed from. Comparing it
    // at the base price would rank a provider on a price the session never pays.
    ModelProviderEntry entry = Entry("Anthropic", 0.000003m, 0.0000003m) with
    {
      PriceOverrides = [new PriceOverride(200_000, 0.000006m, 0.0000006m, 0.0000075m, 0.0000225m)],
    };

    EffectiveInputPrice shortCall = EffectivePrice.For(entry, 0m, promptTokens: 1_000);
    EffectiveInputPrice longCall = EffectivePrice.For(entry, 0m, promptTokens: 300_000);

    Assert.Equal(0.000003m, shortCall.ReadPerInputToken);
    Assert.Equal(0.000006m, longCall.ReadPerInputToken);
    // The write price comes from the same rung, not from the base row.
    Assert.Equal(0.0000075m, longCall.CacheWritePerInputToken);
  }

  [Fact]
  public void For_NegativeAdvertisedPrice_IsNotAPrice()
  {
    // The routing pseudo-models advertise -1, 'set at routing time'. That is not a price, so
    // the effective price is unknown rather than negative.
    EffectiveInputPrice price = EffectivePrice.For(Entry("Router", -1m, -1m), 0.5m);

    Assert.Null(price.ReadPerInputToken);
  }

  private static ModelProviderEntry Entry(string provider, decimal prompt, decimal cacheRead)
      => new("deepseek/deepseek-v4.1-flash-1017", provider, prompt, cacheRead, 0.0000005m,
          128_000, 8_192, SupportsToolUse: true, SupportsVision: false,
          SupportsTextOutput: true, IntelligenceScore: null, CodingScore: null,
          AgenticScore: null, LatencyMs: null, ThroughputTokensPerSec: null,
          Description: null, SupportsImplicitCaching: true);
}
