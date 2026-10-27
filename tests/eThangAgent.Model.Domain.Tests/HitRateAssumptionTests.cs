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
    HitRateAssumption? assumption = HitRateAssumption.From(Report(scope, 0.97m));

    Assert.NotNull(assumption);
    Assert.Equal(0.97m, assumption.Rate);
    Assert.Equal(expected, assumption.Source);
  }

  // A session that has billed no input token has no measured rate. Returning a rate here
  // would let a caller mistake 'nothing measured' for a measurement; the resolution chain
  // decides what applies instead, and it tries the published rate before the default.
  [Fact]
  public void From_AnUnmeasuredReport_IsNull_SoTheCallerDecidesTheFallback()
      => Assert.Null(HitRateAssumption.From(Report(UsageScope.Session, null)));

  [Fact]
  public void From_AZeroRate_IsAMeasurement_NotTheDefault()
  {
    // A real 0% (a provider that reported no cache hits) is a measurement and must not be
    // confused with 'nothing measured'. The source is what keeps them apart.
    HitRateAssumption? assumption = HitRateAssumption.From(Report(UsageScope.Workspace, 0m));

    Assert.NotNull(assumption);
    Assert.Equal(0m, assumption.Rate);
    Assert.Equal(HitRateSource.Workspace, assumption.Source);
  }

  [Fact]
  public void FromPublished_IsItsOwnSource_AndNeverClaimsToBeAMeasurement()
  {
    // The provider's fleet-wide figure is real evidence, but it is not OUR measurement of
    // this session's prompt shapes, and the surface must never present it as one.
    HitRateAssumption assumption = HitRateAssumption.FromPublished(0.629m);

    Assert.Equal(0.629m, assumption.Rate);
    Assert.Equal(HitRateSource.Published, assumption.Source);
    Assert.Contains("OpenRouter published", assumption.Describe(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(-0.01)]
  [InlineData(1.01)]
  public void FromPublished_RejectsARateOutsideTheUnitInterval(double rate)
      // A rate outside [0,1] is a caller's arithmetic bug, not a domain failure: clamping
      // would turn it into a plausible-looking price.
      => _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => HitRateAssumption.FromPublished((decimal)rate));

  [Fact]
  public void Describe_DistinguishesEverySource()
  {
    string[] described =
    [
      .. new[]
      {
        HitRateAssumption.From(Report(UsageScope.Session, 0.9m))!,
        HitRateAssumption.From(Report(UsageScope.Workspace, 0.9m))!,
        HitRateAssumption.From(Report(UsageScope.Global, 0.9m))!,
        HitRateAssumption.FromPublished(0.9m),
        HitRateAssumption.Fallback,
      }.Select(a => a.Describe()),
    ];

    // Five sources, five distinct sentences: a surface that rendered a measured rate, a
    // published rate, and an invented default identically would undo the distinction.
    Assert.Equal(described.Length, described.Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public void IsEvidence_IsFalseOnlyForTheDefault()
  {
    Assert.False(HitRateAssumption.Fallback.IsEvidence);
    Assert.True(HitRateAssumption.FromPublished(0.5m).IsEvidence);
    Assert.True(HitRateAssumption.From(Report(UsageScope.Session, 0m))!.IsEvidence);
  }

  [Fact]
  public void PercentText_RendersOneDecimal()
  {
    Assert.Equal("97.7%", HitRateAssumption.FromPublished(0.977m).PercentText);
    Assert.Equal("100%", HitRateAssumption.FromPublished(1m).PercentText);
    Assert.Equal("0%", HitRateAssumption.FromPublished(0m).PercentText);
  }
}
