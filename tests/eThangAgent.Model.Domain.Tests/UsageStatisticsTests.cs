using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

/// <summary>The arithmetic behind the observed-performance read model (issue #295), tested as
///     PURE functions: the median's outlier resistance (D2), the zero-output exclusion (D3),
///     the derived hit rate (D4), and the percentile the reliability signal reads.
///
///     Anti-tautology: the fixtures are the shapes the issue measured on live data - a long
///     right tail (one 578-second call among typical ones) and a tool-call-only response with
///     no output at all - so a regression that reintroduced a mean, or let a zero-output call
///     into the rate, fails here rather than in a picker nobody re-read.</summary>
public sealed class UsageStatisticsTests
{
  [Fact]
  public void Median_OddCount_IsTheMiddleValue() => Assert.Equal(3d, UsageStatistics.Median([5d, 1d, 3d]));

  [Fact]
  public void Median_EvenCount_AveragesTheTwoCentralValues()
      => Assert.Equal(4d, UsageStatistics.Median([1d, 3d, 5d, 7d]));

  [Fact]
  public void Median_NoMeasurements_IsNullNotZero()
  {
    // Null is 'not measured'; zero would rank a provider nobody measured as instant.
    Assert.Null(UsageStatistics.Median([]));
    Assert.Null(UsageStatistics.Median(null));
  }

  [Fact]
  public void Median_OneExtremeOutlier_DoesNotMoveIt()
  {
    // B2, and the reason D2 chose a median: a 578-second call measured in this workspace
    // would move a mean by seconds while saying nothing about the typical experience.
    double[] durations = [900d, 1000d, 1100d, 1200d, 578_000d];

    Assert.Equal(1100d, UsageStatistics.Median(durations));
    // The mean of the same set is what this refuses to report.
    Assert.True(durations.Average() > 100_000d);
  }

  [Fact]
  public void Median_DoesNotReorderTheCallersList()
  {
    // A measurement helper that sorted its caller's data in place would be a surprise
    // nobody asked for, and callers hold these lists across calls.
    double[] durations = [3d, 1d, 2d];

    _ = UsageStatistics.Median(durations);

    Assert.Equal([3d, 1d, 2d], durations);
  }

  [Fact]
  public void OutputTokensPerSecond_ZeroOutput_IsExcluded()
      // B3: a tool-call-only response produced no output, so it has no rate. Letting it
      // through would drag the median toward zero and describe a fast provider as slow.
      => Assert.Null(UsageStatistics.OutputTokensPerSecond(0, 1000));

  [Fact]
  public void OutputTokensPerSecond_UnmeasuredDuration_IsNullNotZero()
      => Assert.Null(UsageStatistics.OutputTokensPerSecond(500, null));

  [Fact]
  public void OutputTokensPerSecond_ZeroDuration_IsExcludedRatherThanInfinite()
      // A zero duration with real output is a degenerate measurement (the clock's resolution
      // ran out); dividing by it would report an infinite rate.
      => Assert.Null(UsageStatistics.OutputTokensPerSecond(500, 0));

  [Fact]
  public void OutputTokensPerSecond_MeasuredCall_IsTokensOverSeconds()
      // 500 tokens in 2000 ms is 250 tokens/second.
      => Assert.Equal(250d, UsageStatistics.OutputTokensPerSecond(500, 2000));

  [Fact]
  public void HitRate_IsCachedOverInput()
      // B4: the picker's projection and the status bar's rollup must be the same number, so
      // the ratio is derived from the counts rather than carried beside them.
      => Assert.Equal(0.9m, UsageStatistics.HitRate(900, 1000));

  [Fact]
  public void HitRate_NoInputTokens_IsNull() => Assert.Null(UsageStatistics.HitRate(0, 0));

  [Fact]
  public void HitRate_MoreCachedThanInput_IsClampedToOne()
      // A provider reporting more cached tokens than input tokens has reported something
      // impossible; a rate above 1 would make EffectivePrice project a negative uncached share.
      => Assert.Equal(1m, UsageStatistics.HitRate(2000, 1000));

  [Fact]
  public void Percentile_P90_ReadsARealSampleRatherThanAnInterpolatedOne()
  {
    // Nearest-rank: with ten samples the p90 is the ninth value, a duration a call actually
    // took. An interpolated percentile would report a duration nobody measured.
    double[] durations = [100d, 200d, 300d, 400d, 500d, 600d, 700d, 800d, 900d, 10_000d];

    Assert.Equal(900d, UsageStatistics.Percentile(durations, 0.9));
  }

  [Fact]
  public void Percentile_FewSamples_ClampsIntoRange()
  {
    Assert.Equal(500d, UsageStatistics.Percentile([100d, 500d], 0.9));
    Assert.Null(UsageStatistics.Percentile([], 0.9));
  }

  [Fact]
  public void Percentile_OutOfRange_Throws()
  {
    // A programmer error, not a domain failure: no caller can legitimately ask for the 0th
    // percentile, and clamping it silently would hide the bug.
    _ = Assert.Throws<ArgumentOutOfRangeException>(() => UsageStatistics.Percentile([1d], 0d));
    _ = Assert.Throws<ArgumentOutOfRangeException>(() => UsageStatistics.Percentile([1d], 1.5d));
  }
}
