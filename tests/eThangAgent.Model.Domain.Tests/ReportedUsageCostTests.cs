namespace eThangAgent.ModelDomain.Tests;

public class ReportedUsageCostTests
{
  private static UsageRow Row(decimal? reported, bool? expectsReported) =>
      new("main_loop", "model", null, null, 100, 10, null, null, 0.01m, null, 0.02m,
          ReportedCost: reported, ExpectsReportedCost: expectsReported);

  [Fact]
  public void ReportedCharge_WinsOverEstimate()
  {
    Assert.Equal(0.07m, UsageCost.Of(Row(0.07m, true)));
    Assert.Equal(0m, UsageCost.Of(Row(0m, true)));
  }

  [Fact]
  public void MissingCharge_IsUnknownEvenWithCatalogPrices()
      => Assert.Null(UsageCost.Of(Row(null, true)));

  [Fact]
  public void HistoricalEstimates_AreCountedSeparately()
  {
    UsageTally tally = new();
    tally.Add(Row(null, null));
    tally.Add(Row(0.07m, true));
    tally.Add(Row(null, true));
    Assert.Equal(1, tally.Totals.EstimatedCalls);
    Assert.Equal(1, tally.Totals.UnpricedCalls);
  }
}
