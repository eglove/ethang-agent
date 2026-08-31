namespace eThangAgent.AgentDomain.Tests;

public class WatchdogPolicyTests
{
  private static WatchdogPolicy Policy() => new(
      IdleThreshold: TimeSpan.FromMinutes(15),
      SettleWait: TimeSpan.FromSeconds(60),
      MaxWrapUpAttempts: 1);

  [Fact]
  public void Decide_RootAgent_AlwaysWatches()
  {
    Assert.Equal(WatchdogPolicyDecision.Watch,
        Policy().Decide(isChild: false, idleAge: TimeSpan.FromHours(9), wrapUpAttempts: 0));
  }

  [Fact]
  public void Decide_ChildUnderThreshold_Watches()
  {
    Assert.Equal(WatchdogPolicyDecision.Watch,
        Policy().Decide(isChild: true, idleAge: TimeSpan.FromMinutes(14), wrapUpAttempts: 0));
  }

  [Fact]
  public void Decide_ChildAtThresholdNoAttempts_RetriesWrapUp()
  {
    Assert.Equal(WatchdogPolicyDecision.RetryWrapUp,
        Policy().Decide(isChild: true, idleAge: TimeSpan.FromMinutes(15), wrapUpAttempts: 0));
  }

  [Fact]
  public void Decide_ChildAtThresholdWithMaxAttempts_TerminalReport()
  {
    Assert.Equal(WatchdogPolicyDecision.TerminalReport,
        Policy().Decide(isChild: true, idleAge: TimeSpan.FromMinutes(15), wrapUpAttempts: 1));
  }

  [Fact]
  public void Decide_ChildNoHeartbeatEntry_UsesNullAgeAndWatches()
  {
    // null idle age = no heartbeat entry: the store fallback (CreatedAt) is applied by the
    // caller; the policy treats unknown as Watch so a fresh spawn is never instantly flagged.
    Assert.Equal(WatchdogPolicyDecision.Watch,
        Policy().Decide(isChild: true, idleAge: null, wrapUpAttempts: 0));
  }

  [Fact]
  public void WrapUpNudge_ContainsIdleMinutes()
  {
    string nudge = WatchdogPolicy.WrapUpNudge(16);
    Assert.Contains("16", nudge, StringComparison.Ordinal);
    Assert.Contains("wrap up", nudge, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Constructor_NegativeAttempts_Throws()
  {
    _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
        new WatchdogPolicy(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), -1));
  }
}