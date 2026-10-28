namespace eThangAgent.ModelDomain.Tests;

// The Cache Floor order: endpoints sorted by the provider's advertised cache-read price,
// ascending, with unknown prices last and a deterministic tiebreak. The list names the
// routable endpoint slugs a request's provider.order carries.
public class CacheFloorOrderTests
{
  private static ModelProviderEntry Endpoint(string slug, decimal? cacheRead,
      decimal prompt = 0.0000001m)
      => new("test/model", "Provider", prompt, cacheRead, 0.0000005m, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
      {
        Variant = new EndpointVariant(slug),
      };

  [Fact]
  public void Slugs_OrdersByCacheReadPriceAscending()
  {
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("b/fp8", 0.00000030m),
        Endpoint("a/fp4", 0.00000002m),
        Endpoint("c/fp8", 0.00000010m),
    ];

    Assert.Equal(["a/fp4", "c/fp8", "b/fp8"], CacheFloorOrder.Slugs(endpoints));
  }

  [Fact]
  public void Slugs_SortsAnUnknownCacheReadPriceLast()
  {
    // Unknown is never cheapest: an endpoint that advertises no cache-read price cannot be
    // shown to be the cheapest, so it must not lead the order.
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("unknown", null),
        Endpoint("known", 0.00000010m),
    ];

    Assert.Equal(["known", "unknown"], CacheFloorOrder.Slugs(endpoints));
  }

  [Fact]
  public void Slugs_SortsANegativeCacheReadPriceLast()
  {
    // The routing pseudo-models advertise -1, "set at routing time" - not a price.
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("routing", -1m),
        Endpoint("known", 0.00000010m),
    ];

    Assert.Equal(["known", "routing"], CacheFloorOrder.Slugs(endpoints));
  }

  [Fact]
  public void Slugs_KeepsAServedZeroFirst_BecauseZeroIsARealPrice()
  {
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("paid", 0.00000010m),
        Endpoint("free", 0m),
    ];

    Assert.Equal(["free", "paid"], CacheFloorOrder.Slugs(endpoints));
  }

  [Fact]
  public void Slugs_BreaksATieOnInputPriceThenSlug()
  {
    // 50.9% of multi-endpoint models have a tied cheapest cache-read price, so the tiebreak
    // is load-bearing rather than cosmetic.
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("z/fp8", 0.00000002m, prompt: 0.00000020m),
        Endpoint("a/fp8", 0.00000002m, prompt: 0.00000010m),
        Endpoint("m/fp8", 0.00000002m, prompt: 0.00000010m),
    ];

    Assert.Equal(["a/fp8", "m/fp8", "z/fp8"], CacheFloorOrder.Slugs(endpoints));
  }

  [Fact]
  public void Slugs_CapsTheListAndKeepsTheCheapest()
  {
    IReadOnlyList<ModelProviderEntry> endpoints =
    [
        Endpoint("d", 0.00000040m),
        Endpoint("a", 0.00000010m),
        Endpoint("c", 0.00000030m),
        Endpoint("b", 0.00000020m),
    ];

    Assert.Equal(["a", "b"], CacheFloorOrder.Slugs(endpoints, maxEndpoints: 2));
  }

  [Fact]
  public void Slugs_DropsEndpointsWithNoRoutableSlug()
  {
    // An endpoint the API named no tag for cannot be named in an order list.
    ModelProviderEntry unnamed = new("test/model", "Provider", 0.0000001m, 0.00000001m,
        0.0000005m, 128_000, 8_192, SupportsToolUse: true, SupportsVision: false,
        SupportsTextOutput: true, IntelligenceScore: null, CodingScore: null,
        AgenticScore: null, LatencyMs: null, ThroughputTokensPerSec: null, Description: null);

    Assert.Empty(CacheFloorOrder.Slugs([unnamed]));
  }

  [Fact]
  public void Slugs_EmptyCatalog_ReturnsEmpty()
      => Assert.Empty(CacheFloorOrder.Slugs([]));

  [Fact]
  public void Slugs_ZeroCap_Throws()
      => Assert.Throws<ArgumentOutOfRangeException>(() => CacheFloorOrder.Slugs([], maxEndpoints: 0));
}
