using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class MeasuredMixPriorTests
{
  [Fact]
  public void RoundTrip_PreservesAllMembers()
  {
    MeasuredMixPrior prior = new(1200L, 3600L, 7, DateTimeOffset.Parse("2026-01-02T03:04:05+00:00", System.Globalization.CultureInfo.InvariantCulture));

    bool parsed = MeasuredMixPrior.TryFromJson(prior.ToJson(), out MeasuredMixPrior round);

    Assert.True(parsed);
    Assert.Equal(prior, round);
  }

  [Fact]
  public void TryFromJson_CorruptJson_ReturnsFalse()
  {
    bool parsed = MeasuredMixPrior.TryFromJson("{ not json", out MeasuredMixPrior prior);

    Assert.False(parsed);
    Assert.Equal(0L, prior.TotalInput);
    Assert.Equal(0L, prior.TotalOutput);
    Assert.Equal(0, prior.TurnCount);
  }

  [Fact]
  public void SeedWeight_ZeroInput_DefaultWeight()
  {
    MeasuredMixPrior prior = new(0L, 5000L, 3, DateTimeOffset.UtcNow);

    Assert.Equal(MeasuredMixPolicy.DefaultWeight, prior.SeedWeight);
  }

  [Fact]
  public void SeedWeight_HugeOutput_ClampedTo50()
  {
    MeasuredMixPrior prior = new(10L, 100000L, 3, DateTimeOffset.UtcNow);

    Assert.Equal(50m, prior.SeedWeight);
  }

  [Fact]
  public void StateKey_Format()
      => Assert.Equal("measured_mix:openrouter:C:/work", MeasuredMixPrior.StateKey("openrouter", "C:/work"));
}
