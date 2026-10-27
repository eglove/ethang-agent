using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>The read model's bridge into provider ranking (issue #295): the report's
///     measurements must arrive at <see cref="ProviderRanking"/> as OBSERVATIONS, and a pair
///     nothing measured must arrive as null rather than as a zero - the difference between
///     'unmeasured' and 'instant'.
///
///     Anti-tautology: this drives the REAL ranking function over the REAL report shape, so a
///     bridge that quietly substituted zeros would reorder the very comparison it feeds and
///     fail here rather than in a picker nobody re-read.</summary>
public sealed class ProviderPerformanceRankingTests
{
  /// <summary>One catalog row for the pair under test. Positional because the record has no
  ///     parameterless form; the capability members are irrelevant to a latency comparison and
  ///     are set to the values a text chat model would carry.</summary>
  private static ModelProviderEntry Entry(string modelId, string? providerName, decimal promptPrice,
      decimal cacheReadPrice) => new(modelId, providerName, promptPrice, cacheReadPrice,
      0.000002m, 200_000, 4096, SupportsToolUse: true, SupportsVision: false,
      SupportsTextOutput: true, IntelligenceScore: null, CodingScore: null, AgenticScore: null,
      LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
      SupportsImplicitCaching: true);

  private static ProviderPerformanceReport Report(params ProviderPerformance[] rows) =>
      new(UsageScope.Session, 0.9m, 1000, 900, rows.Sum(row => row.Calls), 0, rows);

  [Fact]
  public void Rankable_CarriesTheMeasuredMedianAsAnObservation()
  {
    ProviderPerformanceReport report = Report(new ProviderPerformance("model/x", "vendor-a",
        1200d, 1500d, 300d, 40d, 10, 10, 10, 0, 10, 1000, 900));
    ModelProviderEntry entry = Entry("model/x", "vendor-a", 0.000001m, 0.0000001m);

    RankableProvider rankable = Assert.Single(report.Rankable([entry]));

    Assert.Equal(1200d, rankable.ObservedLatencyMs);
    Assert.Equal(40d, rankable.ObservedThroughputTokensPerSec);
  }

  [Fact]
  public void Rankable_UnmeasuredPair_CarriesNullsNotZeros()
  {
    // A pair the ledger never timed must arrive unmeasured. A zero would rank it as the
    // fastest provider in a latency sort.
    ProviderPerformanceReport report = ProviderPerformanceReport.Empty(UsageScope.Session);
    ModelProviderEntry entry = Entry("model/x", "vendor-a", 0.000001m, 0.0000001m);

    RankableProvider rankable = Assert.Single(report.Rankable([entry]));

    Assert.Null(rankable.ObservedLatencyMs);
    Assert.Null(rankable.ObservedThroughputTokensPerSec);
  }

  [Fact]
  public void Rankable_MeasuredProviderOutranksAnUnmeasuredOneOnLatency()
  {
    // The end-to-end reason this bridge exists: ranking on latency must prefer the provider
    // we measured as fast over one nobody measured, in that order and not the reverse.
    ProviderPerformanceReport report = Report(new ProviderPerformance("model/x", "measured",
        800d, 900d, 200d, 50d, 20, 20, 20, 0, 20, 1000, 900));
    ModelProviderEntry measured = Entry("model/x", "measured", 0.000001m, 0.0000001m);
    ModelProviderEntry unmeasured = Entry("model/x", "unmeasured", 0.000001m, 0.0000001m);

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(
        report.Rankable([unmeasured, measured]),
        new RankingCriteria([ProviderPriority.Ascending(ProviderDimension.Latency)], 0.9m));

    Assert.Equal("measured", ranked[0].Entry.ProviderName);
    Assert.Equal("unmeasured", ranked[1].Entry.ProviderName);
  }

  [Fact]
  public void Rankable_MatchesOnThePair_NotTheProviderAlone()
  {
    // Latency is a property of the model AND the provider: the same vendor on another model
    // must not inherit these measurements.
    ProviderPerformanceReport report = Report(new ProviderPerformance("model/x", "vendor-a",
        1200d, 1500d, 300d, 40d, 10, 10, 10, 0, 10, 1000, 900));
    ModelProviderEntry otherModel = Entry("model/y", "vendor-a", 0.000001m, 0.0000001m);

    RankableProvider rankable = Assert.Single(report.Rankable([otherModel]));

    Assert.Null(rankable.ObservedLatencyMs);
  }

  [Fact]
  public void Rankable_UnnamedProviderRow_DoesNotBindToANamedOne()
  {
    // A fabricated provider name once rode selection onto the wire as a provider pin. An
    // unnamed catalog row matches only the report's unnamed bucket, by exact equality.
    ProviderPerformanceReport report = Report(new ProviderPerformance("model/x", "vendor-a",
        1200d, 1500d, 300d, 40d, 10, 10, 10, 0, 10, 1000, 900));
    ModelProviderEntry unnamed = Entry("model/x", null, 0.000001m, 0.0000001m);

    RankableProvider rankable = Assert.Single(report.Rankable([unnamed]));

    Assert.Null(rankable.ObservedLatencyMs);
  }
}
