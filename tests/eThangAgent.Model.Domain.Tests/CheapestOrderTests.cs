namespace eThangAgent.ModelDomain.Tests;

// The Cheapest order: endpoints ranked by true total cost at a stated usage shape, capped at
// eight, with the fragility claim that says whether the winner holds at every shape or only at
// this one. The list names the routable endpoint slugs a request's provider.order carries.
public class CheapestOrderTests
{
  // The shape the workspace ledger measures: 90.13% cache hits, 0.007284 output tokens per input
  // token. Every figure in this file is computed at it unless a test says otherwise.
  private static readonly CostShape WorkspaceShape = new(0.9013m, 0.007284m);

  private static ModelProviderEntry Endpoint(string? slug, decimal? input, decimal? cacheRead,
      decimal? output)
      => new("test/model", "Provider", input, cacheRead, output, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null)
      {
        Variant = slug is null ? EndpointVariant.Unspecified : new EndpointVariant(slug),
      };

  // E1: the live deepseek/deepseek-v4.1-flash pair at the workspace shape.
  private static ModelProviderEntry Wafer => Endpoint("wafer/fp8", 5.1e-8m, 1.8e-8m, 5.67e-7m);

  private static ModelProviderEntry River => Endpoint("river/fp8", 9.6e-8m, 1.92e-9m, 3.84e-7m);

  [Fact]
  public void For_LeadsWithTheCheapestEndpointAtTheShape()
  {
    // Wafer costs 0.025387 and River 0.014003 USD per 1M input tokens at this shape, so River
    // leads - even though Wafer is cheaper on the input rate alone.
    CheapestRanking ranking = CheapestOrder.For([Wafer, River], WorkspaceShape);

    Assert.Equal(["river/fp8", "wafer/fp8"], ranking.Slugs);
    Assert.Equal("river/fp8", ranking.WinnerSlug);
    Assert.Equal("wafer/fp8", ranking.RunnerUpSlug);
  }

  [Fact]
  public void For_ReportsFragilityAndTheBreakEvenHitRateWhenTheWinnerDoesNotDominate()
  {
    // River wins at the workspace shape but Wafer is cheaper on the input rate, so River does
    // not dominate: the answer depends on the shape and the ranking says so, naming the 71.49%
    // cache-hit rate at which the two cost the same.
    CheapestRanking ranking = CheapestOrder.For([Wafer, River], WorkspaceShape);

    Assert.True(ranking.IsFragile);
    Assert.Equal(0.7149m, decimal.Round(ranking.BreakEvenHitRate!.Value, 4));
  }

  [Fact]
  public void For_IsNotFragileWhenTheWinnerDominatesTheRunnerUp()
  {
    // E2: A is no more expensive on any of the three rates and strictly cheaper on all of them,
    // so it is cheapest at every hit rate and every ratio - no estimate was made.
    ModelProviderEntry a = Endpoint("a/fp8", 1e-7m, 1e-8m, 2e-7m);
    ModelProviderEntry b = Endpoint("b/fp8", 2e-7m, 3e-8m, 4e-7m);

    CheapestRanking ranking = CheapestOrder.For([b, a], WorkspaceShape);

    Assert.Equal(["a/fp8", "b/fp8"], ranking.Slugs);
    Assert.Equal("a/fp8", ranking.WinnerSlug);
    Assert.False(ranking.IsFragile);
    Assert.Null(ranking.BreakEvenHitRate);
  }

  [Fact]
  public void For_IsNotFragileWhenNeitherEndpointCaches()
  {
    // E3: an endpoint with no cache-read rate costs 0.308741 USD per 1M at every hit rate, and
    // against a rival that also does not cache the order is decided by the input and output
    // rates alone - there is no hit rate at which the answer moves.
    ModelProviderEntry uncached = Endpoint("uncached/fp8", 3e-7m, null, 1.2e-6m);
    ModelProviderEntry rival = Endpoint("rival/fp8", 4e-7m, null, 1.5e-6m);

    CheapestRanking ranking = CheapestOrder.For([rival, uncached], WorkspaceShape);

    Assert.Equal(["uncached/fp8", "rival/fp8"], ranking.Slugs);
    Assert.Equal("uncached/fp8", ranking.WinnerSlug);
    Assert.False(ranking.IsFragile);
    Assert.Null(ranking.BreakEvenHitRate);
  }

  [Fact]
  public void For_SortsAnUnknownInputPriceLastAndNeverNamesItTheWinner()
  {
    // E4: the unpriced row is cheaper on both rates it advertises, and it still cannot lead -
    // unknown is never cheapest.
    ModelProviderEntry unpriced = Endpoint("unpriced/fp8", null, 1e-9m, 1e-9m);
    ModelProviderEntry priced = Endpoint("priced/fp8", 5e-7m, 5e-7m, 5e-6m);

    CheapestRanking ranking = CheapestOrder.For([unpriced, priced], WorkspaceShape);

    Assert.Equal(["priced/fp8", "unpriced/fp8"], ranking.Slugs);
    Assert.Equal("priced/fp8", ranking.WinnerSlug);
    // The unpriced row is not the runner-up either: there is no second PRICED endpoint.
    Assert.Null(ranking.RunnerUpSlug);
    Assert.False(ranking.IsFragile);
  }

  [Fact]
  public void For_DropsARowWithNoEndpointTag()
  {
    // A row whose price describes no distinct endpoint cannot be named in an order. It is
    // dropped rather than ranked last, because the order exists to name endpoints.
    ModelProviderEntry unnamed = Endpoint(null, 1e-9m, 1e-9m, 1e-9m);

    CheapestRanking ranking = CheapestOrder.For([unnamed, River], WorkspaceShape);

    Assert.Equal(["river/fp8"], ranking.Slugs);
    Assert.Equal("river/fp8", ranking.WinnerSlug);
  }

  [Fact]
  public void For_CapsTheListAtEightAndKeepsTheWinnerAndRunnerUp()
  {
    // E5: a model can carry 37 endpoints, and sending all of them bloats every request. The
    // four most expensive are omitted; the two that decide the answer never are.
    List<ModelProviderEntry> endpoints = [];
    for (int i = 0; i < 12; i++)
    {
      decimal input = 1e-7m * (i + 1);
      endpoints.Add(Endpoint($"e{i:D2}/fp8", input, input / 10m, input * 2m));
    }

    CheapestRanking ranking = CheapestOrder.For(endpoints, WorkspaceShape);

    Assert.Equal(CheapestOrder.MaxOrderedEndpoints, ranking.Slugs.Count);
    Assert.Equal(8, ranking.Slugs.Count);
    Assert.Equal(["e00/fp8", "e01/fp8", "e02/fp8", "e03/fp8", "e04/fp8", "e05/fp8",
      "e06/fp8", "e07/fp8"], ranking.Slugs);
    Assert.Equal("e00/fp8", ranking.WinnerSlug);
    Assert.Equal("e01/fp8", ranking.RunnerUpSlug);
  }

  [Fact]
  public void For_BreaksATieOnInputRateThenSlugOrdinally()
  {
    // E6: identical rates order by slug, ordinal, and the call is repeatable - the list is part
    // of a persisted preference, so two identical inputs must produce an identical order.
    ModelProviderEntry zed = Endpoint("z/fp8", 1e-7m, 1e-8m, 2e-7m);
    ModelProviderEntry alpha = Endpoint("a/fp8", 1e-7m, 1e-8m, 2e-7m);
    ModelProviderEntry middle = Endpoint("m/fp8", 1e-7m, 1e-8m, 2e-7m);

    CheapestRanking first = CheapestOrder.For([zed, middle, alpha], WorkspaceShape);
    CheapestRanking again = CheapestOrder.For([alpha, zed, middle], WorkspaceShape);

    Assert.Equal(["a/fp8", "m/fp8", "z/fp8"], first.Slugs);
    Assert.Equal(first.Slugs, again.Slugs);
    Assert.Equal(first.WinnerSlug, again.WinnerSlug);
    Assert.Equal(first.RunnerUpSlug, again.RunnerUpSlug);
  }

  [Fact]
  public void For_BreaksATieOnInputRateBeforeSlug()
  {
    // The tiebreak is unit cost, then input rate, then slug: two rows with the same unit cost
    // and different input rates are ordered by the rate, not by the slug.
    ModelProviderEntry cheaperInput = Endpoint("z/fp8", 1e-7m, 1e-8m, 2e-7m);
    ModelProviderEntry dearerInput = Endpoint("a/fp8", 2e-7m, 2e-8m, 4e-7m);

    CheapestRanking ranking = CheapestOrder.For([dearerInput, cheaperInput], WorkspaceShape);

    Assert.Equal(["z/fp8", "a/fp8"], ranking.Slugs);
  }

  [Fact]
  public void For_NeverReturnsAnEmptyOrNullSlug()
  {
    ModelProviderEntry unnamed = Endpoint(null, 1e-9m, 1e-9m, 1e-9m);
    ModelProviderEntry blank = Endpoint("", 1e-9m, 1e-9m, 1e-9m);

    CheapestRanking ranking = CheapestOrder.For([unnamed, blank, River], WorkspaceShape);

    Assert.All(ranking.Slugs, slug => Assert.False(string.IsNullOrEmpty(slug)));
    Assert.True(ranking.Slugs.Count <= CheapestOrder.MaxOrderedEndpoints);
  }

  [Fact]
  public void For_EmptyCatalog_ReturnsAnEmptyRanking()
  {
    CheapestRanking ranking = CheapestOrder.For([], WorkspaceShape);

    Assert.Empty(ranking.Slugs);
    Assert.Null(ranking.WinnerSlug);
    Assert.Null(ranking.RunnerUpSlug);
    Assert.Null(ranking.BreakEvenHitRate);
    Assert.False(ranking.IsFragile);
  }

  [Fact]
  public void For_OnePricedEndpoint_HasNothingToBeFragileAgainst()
  {
    // A single priced endpoint is the only endpoint whose cost is known, so no shape-dependent
    // choice was made and there is no runner-up to cross.
    CheapestRanking ranking = CheapestOrder.For([River], WorkspaceShape);

    Assert.Equal(["river/fp8"], ranking.Slugs);
    Assert.Equal("river/fp8", ranking.WinnerSlug);
    Assert.Null(ranking.RunnerUpSlug);
    Assert.False(ranking.IsFragile);
    Assert.Null(ranking.BreakEvenHitRate);
  }

  [Fact]
  public void For_IsFragileWithNoReachableCrossing_ReportsNoBreakEvenRate()
  {
    // The winner does not dominate, yet the two never cost the same inside [0,1]: the answer is
    // still shape-dependent, so it is fragile, but there is no turning point to name.
    ModelProviderEntry winner = Endpoint("a/fp8", 1e-7m, 1.0000002e-6m, 1e-7m);
    ModelProviderEntry rival = Endpoint("b/fp8", 1.1e-6m, 1e-6m, 2e-7m);

    CheapestRanking ranking = CheapestOrder.For([winner, rival], WorkspaceShape);

    Assert.Equal("a/fp8", ranking.WinnerSlug);
    Assert.True(ranking.IsFragile);
    Assert.Null(ranking.BreakEvenHitRate);
  }

  [Fact]
  public void For_NullCatalog_Throws()
      => Assert.Throws<ArgumentNullException>(() => CheapestOrder.For(null!, WorkspaceShape));
}
