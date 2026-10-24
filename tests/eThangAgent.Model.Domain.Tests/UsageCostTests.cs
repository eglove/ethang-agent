namespace eThangAgent.ModelDomain.Tests;

// The ledger's ONE cost formula (issue #24, parent decision 5) and the accumulator the
// status bar advances. The formula charges the cached share at the CACHE-READ price:
// charging every input token at the prompt price overstated this workspace's real spend
// by ~8x at its ~98% cache-hit rate.
public class UsageCostTests
{
  private static UsageRow Row(int input, int output, int? cached, decimal? prompt, decimal? cacheRead,
      decimal? completion)
      => new("main_loop", "test/model", "openrouter", "ws-1", input, output, cached, null,
          prompt, cacheRead, completion);

  [Fact]
  public void Of_ChargesTheCachedShareAtTheCacheReadPrice()
  {
    // 100 uncached x 0.000002 = 0.0002; 900 cached x 0.0000002 = 0.00018;
    // 100 output x 0.000004 = 0.0004. Total 0.00078 - NOT 1000 x 0.000002 + 100 x 0.000004.
    UsageRow row = Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m);

    Assert.Equal(0.00078m, UsageCost.Of(row));
  }

  [Fact]
  public void Of_WithoutACachePrice_IsNullWhenTheCallHadCachedTokens()
  {
    // The catalog publishes no cache-read price for this model. Charging the cached share
    // at the prompt price would be the old overestimate; charging it at zero would be an
    // understatement. The row is UNPRICED instead - tokens counted, cost unknown.
    UsageRow row = Row(1000, 100, 900, 0.000002m, null, 0.000004m);

    Assert.Null(UsageCost.Of(row));
  }

  [Fact]
  public void Of_WithoutACachePrice_StillPricesACallWithNoCachedTokens()
  {
    // A call that reported no cache hit needs no cache price: every input token is
    // uncached, so the two prices it does need are enough.
    UsageRow row = Row(1000, 100, null, 0.000002m, null, 0.000004m);

    Assert.Equal(0.0024m, UsageCost.Of(row));
  }

  [Fact]
  public void Of_UnknownPromptPrice_IsNull()
  {
    UsageRow row = Row(1000, 100, null, null, 0.0000002m, 0.000004m);

    Assert.Null(UsageCost.Of(row));
  }

  [Fact]
  public void Of_UnknownCompletionPrice_IsNull()
  {
    UsageRow row = Row(1000, 100, null, 0.000002m, null, null);

    Assert.Null(UsageCost.Of(row));
  }

  [Fact]
  public void Of_ZeroPrices_AreRealPricesAndCostZero()
  {
    // A served zero is a price (a free model), not an unknown: it must cost zero rather
    // than read as unpriced.
    UsageRow row = Row(1000, 100, 900, 0m, 0m, 0m);

    Assert.Equal(0m, UsageCost.Of(row));
  }

  [Fact]
  public void Of_MoreCachedThanInputTokens_ClampsInsteadOfGoingNegative()
  {
    // A provider report claiming more cached tokens than input tokens is nonsense. The
    // arithmetic stays defined (uncached clamps to zero) rather than inventing a negative
    // charge that would discount the session.
    UsageRow row = Row(100, 10, 500, 0.000002m, 0.0000002m, 0.000004m);

    Assert.Equal(0.00014m, UsageCost.Of(row));
  }

  [Fact]
  public void Tally_SeedThenAdd_AccumulatesBoth()
  {
    // The tab-open seed is the persisted history; the increments are the live calls. A
    // resumed session must show both, and the seed must not be lost to the first event.
    UsageTally tally = new();
    tally.Seed(new UsageTotal(1000, 100, 800, 0, 0.0005m, 2, 0));
    tally.Add(Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m));

    UsageTotal totals = tally.Totals;
    Assert.Equal(2000, totals.InputTokens);
    Assert.Equal(200, totals.OutputTokens);
    Assert.Equal(0.00128m, totals.Cost);
    Assert.Equal(3, totals.Calls);
  }

  [Fact]
  public void Tally_UnpricedRows_ContributeTokensAndCountAsUnpriced()
  {
    UsageTally tally = new();
    tally.Add(Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m));
    tally.Add(Row(4000, 400, null, null, null, null));

    UsageTotal totals = tally.Totals;
    Assert.Equal(5000, totals.InputTokens);
    Assert.Equal(0.00078m, totals.Cost);
    Assert.Equal(1, totals.PricedCalls);
    Assert.Equal(1, totals.UnpricedCalls);
  }

  [Fact]
  public void Tally_Empty_ReportsNoRowsAndNullCost()
  {
    UsageTotal totals = new UsageTally().Totals;

    Assert.Equal(0, totals.Calls);
    Assert.Null(totals.Cost);
  }
}
