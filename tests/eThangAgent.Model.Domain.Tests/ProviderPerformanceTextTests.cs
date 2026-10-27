using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

// B5 (issue #293, D1): the dead-field rule, pinned as a CONTRACT rather than left to each
// call site. The catalog's latency and throughput fields were null on all 52 endpoints
// measured live, so a surface that formatted them with a plain numeric format would print
// '0 ms' and tell the user every provider responds instantly. A null is 'not measured'; a
// real 0 is a real measurement, and the two must never collapse into each other.
public class ProviderPerformanceTextTests
{
  [Fact]
  public void Latency_Null_RendersNotMeasured_NeverZero()
      => Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.Latency(null));

  [Fact]
  public void Latency_RealZero_IsAMeasurement_NotAnAbsence()
  {
    // A call that finished inside a millisecond reports 0, which is a fact. Rendering it as
    // 'not measured' would throw away the fastest measurement there is.
    string text = ProviderPerformanceText.Latency(0);

    Assert.Equal("0 ms", text);
    Assert.NotEqual(ProviderPerformanceText.NotMeasured, text);
  }

  [Fact]
  // Catalog latency is a double while a ledger duration is whole milliseconds, so the cell
  // rounds. Midpoint values round away from zero (412.5 -> 413), which the second assertion
  // pins so a future format change cannot silently switch to banker's rounding.
  public void Latency_Measured_RendersWholeMilliseconds()
  {
    Assert.Equal("412 ms", ProviderPerformanceText.Latency(412.4));
    Assert.Equal("413 ms", ProviderPerformanceText.Latency(412.5));
  }

  [Fact]
  public void Throughput_Null_RendersNotMeasured()
      => Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.Throughput(null));


  [Fact]
  public void Throughput_Measured_RendersTokensPerSecond()
      => Assert.Equal("88 tok/s", ProviderPerformanceText.Throughput(88.2));

  [Fact]
  public void Uptime_Null_RendersNotMeasured()
      => Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.Uptime(null));

  [Fact]
  public void Uptime_Measured_KeepsOneDecimal_So99_9IsNotRoundedTo100()
  {
    // The difference between 99.9% and 100% is the difference between a provider that drops
    // requests and one that does not.
    Assert.Equal("99.9%", ProviderPerformanceText.Uptime(99.9));
    Assert.Equal("100%", ProviderPerformanceText.Uptime(100));
  }

  [Fact]
  // The live median cache-read ratio is 0.10 - a 90% discount - which is the number that
  // makes a cache price meaningful at all.
  public void CacheDiscount_KnownPrices_RendersTheFractionSaved()
      => Assert.Equal("90% off", ProviderPerformanceText.CacheDiscount(0.000003m, 0.0000003m));

  [Fact]
  // 45 catalog rows charge MORE for a cache read than for a fresh prompt token. Clamping
  // that to 0% would hide the one case a user most needs to see.
  public void CacheDiscount_CacheReadCostsMoreThanPrompt_StaysAboveZeroAsAPercentage()
      => Assert.Equal("-50% off", ProviderPerformanceText.CacheDiscount(0.000002m, 0.000003m));

  [Fact]
  public void CacheDiscount_MissingEitherPrice_RendersNotMeasured()
  {
    Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.CacheDiscount(null, 0.0000003m));
    Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.CacheDiscount(0.000003m, null));
  }

  [Fact]
  // A free model has no ratio to state: dividing by its prompt price would be a division
  // by zero dressed up as a discount.
  public void CacheDiscount_ZeroPromptPrice_RendersNotMeasured()
      => Assert.Equal(ProviderPerformanceText.NotMeasured, ProviderPerformanceText.CacheDiscount(0m, 0m));
}
