using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

public class CompactionPolicyTests
{
  [Theory]
  [InlineData(null, 80.0, 70.0, 40.0)]      // unresolved window: small-window defaults
  [InlineData(0, 80.0, 70.0, 40.0)]
  [InlineData(128_000, 80.0, 70.0, 40.0)]
  [InlineData(200_000, 80.0, 70.0, 40.0)]   // small-band upper edge (inclusive)
  [InlineData(200_001, 70.0, 60.0, 35.0)]
  [InlineData(500_000, 70.0, 60.0, 35.0)]   // medium-band upper edge (inclusive)
  [InlineData(500_001, 60.0, 50.0, 30.0)]
  [InlineData(1_000_000, 60.0, 50.0, 30.0)] // large-band upper edge (inclusive)
  [InlineData(1_000_001, 50.0, 40.0, 25.0)]
  [InlineData(2_000_000, 50.0, 40.0, 25.0)]
  public void For_DerivesPressuresFromWindowSize(int? window, double ceiling, double soft, double target)
  {
    CompactionPolicy policy = CompactionPolicy.For(window);
    Assert.Equal(ceiling, policy.CeilingPercent);
    Assert.Equal(soft, policy.SoftReminderPercent);
    Assert.Equal(target, policy.TargetPercent);
  }
}
