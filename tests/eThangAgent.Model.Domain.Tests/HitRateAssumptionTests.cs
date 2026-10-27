namespace eThangAgent.ModelDomain.Tests;

/// <summary>The hit-rate assumption (issue #296, D6): the rate a comparison projects prices
///     at always travels with where it came from, and the default is named as a default
///     rather than passed off as a measurement.</summary>
public class HitRateAssumptionTests
{
  private static ProviderPerformanceReport Report(UsageScope scope, decimal? hitRate) =>
      new(scope, hitRate, 1000, 500, 4, 0, []);

  [Theory]
  [InlineData(UsageScope.Session, HitRateSource.Session)]
  [InlineData(UsageScope.Workspace, HitRateSource.Workspace)]
  [InlineData(UsageScope.Global, HitRateSource.Global)]
  public void From_NamesTheRungThatAnswered(UsageScope scope, HitRateSource expected)
  {
    HitRateAssumption assumption = HitRateAssumption.From(Report(scope, 0.97m));

    Assert.Equal(0.97m, assumption.Rate);
    Assert.Equal(expected, assumption.Source);
  }

  [Fact]
  public void From_AnUnmeasuredReport_FallsBackToTheNamedDefault()
  {
    // A session that has billed no input token has no measured rate. Reporting 0% would
    // claim caching never helps, which is the opposite of what an empty ledger says.
    HitRateAssumption assumption = HitRateAssumption.From(Report(UsageScope.Session, null));

    Assert.Equal(HitRateAssumption.DefaultRate, assumption.Rate);
    Assert.Equal(HitRateSource.Default, assumption.Source);
    Assert.Contains("default", assumption.Describe(), StringComparison.Ordinal);
  }

  [Fact]
  public void From_AZeroRate_IsAMeasurement_NotTheDefault()
  {
    // A real 0% (a provider that reported no cache hits) is a measurement and must not be
    // confused with 'nothing measured'. The source is what keeps them apart.
    HitRateAssumption assumption = HitRateAssumption.From(Report(UsageScope.Workspace, 0m));

    Assert.Equal(0m, assumption.Rate);
    Assert.Equal(HitRateSource.Workspace, assumption.Source);
  }

  [Fact]
  public void Manual_IsItsOwnSource()
  {
    HitRateAssumption assumption = HitRateAssumption.Manual(0.25m);

    Assert.Equal(0.25m, assumption.Rate);
    Assert.Equal(HitRateSource.Manual, assumption.Source);
    Assert.Contains("manual", assumption.Describe(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(-0.01)]
  [InlineData(1.01)]
  public void Manual_RejectsARateOutsideTheUnitInterval(double rate)
      // A rate outside [0,1] is a caller's arithmetic bug, not a domain failure: clamping
      // would turn it into a plausible-looking price.
      => _ = Assert.Throws<ArgumentOutOfRangeException>(() => HitRateAssumption.Manual((decimal)rate));

  [Fact]
  public void Describe_DistinguishesEverySource()
  {
    string[] described =
    [
      .. new[]
      {
        HitRateAssumption.From(Report(UsageScope.Session, 0.9m)),
        HitRateAssumption.From(Report(UsageScope.Workspace, 0.9m)),
        HitRateAssumption.From(Report(UsageScope.Global, 0.9m)),
        HitRateAssumption.Fallback,
        HitRateAssumption.Manual(0.9m),
      }.Select(a => a.Describe()),
    ];

    // Five sources, five distinct sentences: a surface that rendered a measured rate and
    // an invented one identically would undo the distinction.
    Assert.Equal(described.Length, described.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public void PercentText_RendersOneDecimal()
  {
    Assert.Equal("97.7%", HitRateAssumption.Manual(0.977m).PercentText);
    Assert.Equal("100%", HitRateAssumption.Manual(1m).PercentText);
    Assert.Equal("0%", HitRateAssumption.Manual(0m).PercentText);
  }
}
