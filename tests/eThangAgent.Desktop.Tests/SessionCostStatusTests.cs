using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The status bar's session cost segment (issue #24, B13): a session with priced
///     spend shows cumulative cost, a session with only unpriced rows shows its token total
///     instead, and a session with no rows shows nothing at all.
///
///     The three states are deliberately distinct: a cost of $0.0000 for spend nobody
///     measured is the same lie as an overstatement, so an unpriced session must never
///     render as free.</summary>
public class SessionCostStatusTests
{
  private static UsageRow Row(int input, int output, int? cached, decimal? prompt, decimal? cacheRead,
      decimal? completion)
      => new("main_loop", "test/model", "openrouter", "ws-1", input, output, cached, null,
          prompt, cacheRead, completion);

  [Fact]
  public void EmptySession_ShowsNoCostSegment()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    Assert.Equal("", status.CostDisplay);
    Assert.Equal("", status.CostBreakdownText);
  }

  [Fact]
  public void PricedSpend_ShowsCumulativeCost()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.AddUsage(Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m));

    // 100 x 0.000002 + 900 x 0.0000002 + 100 x 0.000004 = 0.00078.
    Assert.Equal("~$0.0008", status.CostDisplay);
    Assert.Contains("1 call(s)", status.CostBreakdownText, StringComparison.Ordinal);
    Assert.DoesNotContain("unpriced", status.CostBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public void UnpricedSpend_ShowsTokenTotalsInsteadOfAFabricatedCost()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.AddUsage(Row(12_300, 100, null, null, null, null));

    Assert.Equal("12.4K tok", status.CostDisplay);
    Assert.Contains("unpriced call(s) not included", status.CostBreakdownText,
        StringComparison.Ordinal);
  }

  [Fact]
  public void MixedSpend_ShowsTheCostAndStatesWhatItDoesNotCover()
  {
    // A partial cost is never presented as the whole bill: the breakdown names the calls
    // the number does not include.
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.AddUsage(Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m));
    status.AddUsage(Row(4000, 400, null, null, null, null));

    Assert.Equal("~$0.0008", status.CostDisplay);
    Assert.Contains("1 unpriced call(s) not included", status.CostBreakdownText,
        StringComparison.Ordinal);
  }

  [Fact]
  public void SeedUsage_StartsFromThePersistedRollup()
  {
    // A resumed session starts from its real history, not from zero.
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.SeedUsage(new UsageTotal(1_000_000, 50_000, 900_000, 0, 0.5m, 40, 0));

    // Below a dollar the line keeps four decimals: a single cheap call must be visible
    // at all, and $0.50 is the same number as $0.5000.
    Assert.Equal("$0.5000", status.CostDisplay);
    Assert.Contains("40 call(s)", status.CostBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public void SeedUsage_ThenLiveIncrement_AddsBoth()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    status.SeedUsage(new UsageTotal(1000, 100, 900, 0, 0.0005m, 2, 0));

    status.AddUsage(Row(1000, 100, 900, 0.000002m, 0.0000002m, 0.000004m));

    Assert.Equal("~$0.0013", status.CostDisplay);
    Assert.Contains("3 call(s)", status.CostBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public void ReportedCharge_IsNotAnEstimate()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    status.AddUsage(Row(100, 10, null, 0.1m, null, 0.1m) with { ReportedCost = 0.125m, ExpectsReportedCost = true });
    Assert.Equal("$0.1250", status.CostDisplay);
    Assert.DoesNotContain("estimated", status.CostBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public void MissingReportedCharge_DoesNotUseCatalogEstimate()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");
    status.AddUsage(Row(100, 10, null, 0.1m, null, 0.1m) with { ExpectsReportedCost = true });
    Assert.Equal("110 tok", status.CostDisplay);
  }

  [Fact]
  public void LargeCost_UsesTwoDecimals()
  {
    StatusViewModel status = new("OpenRouter", "m", "Model default");

    status.SeedUsage(new UsageTotal(1_000_000, 50_000, 0, 0, 12.3456m, 10, 0));

    Assert.Equal("$12.35", status.CostDisplay);
  }
}
