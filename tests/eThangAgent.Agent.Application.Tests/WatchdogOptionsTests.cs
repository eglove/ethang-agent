namespace eThangAgent.Agent.Application.Tests;

/// <summary>WatchdogOptions boundary: defaults per spec, invalid values throw, never clamp.</summary>
public class WatchdogOptionsTests
{
  [Fact]
  public void Defaults_MatchSpec()
  {
    WatchdogOptions options = new();
    Assert.True(options.Enabled);
    Assert.Equal(TimeSpan.FromSeconds(60), options.TickInterval);
    Assert.Equal(TimeSpan.FromMinutes(15), options.IdleThreshold);
    Assert.Equal(TimeSpan.FromSeconds(60), options.SettleWait);
    Assert.Equal(1, options.MaxWrapUpAttempts);
    Assert.Equal(4096.0, options.RssThresholdMb);
    Assert.Equal(TimeSpan.FromMinutes(10), options.RssReReportInterval);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void NonPositiveTickInterval_Throws(int seconds)
      => Assert.Throws<ArgumentOutOfRangeException>(() =>
          new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(seconds)));

  [Fact]
  public void NegativeMaxWrapUpAttempts_Throws()
      => Assert.Throws<ArgumentOutOfRangeException>(() =>
          new WatchdogOptions(MaxWrapUpAttempts: -1));

  [Fact]
  public void NonPositiveRssThreshold_Throws()
      => Assert.Throws<ArgumentOutOfRangeException>(() =>
          new WatchdogOptions(RssThresholdMb: 0));

  [Fact]
  public void Default_Singleton_IsValid()
      => Assert.Equal(TimeSpan.FromSeconds(60), WatchdogOptions.Default.TickInterval);
}
