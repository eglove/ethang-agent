using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

public sealed class ResourcePolicyTests
{
  private const long Budget = 1_000_000_000L;

  [Theory]
  [InlineData(0.10, false, ResourceLevel.Normal)]
  [InlineData(0.59, false, ResourceLevel.Normal)]
  [InlineData(0.60, false, ResourceLevel.Notice)]
  [InlineData(0.84, false, ResourceLevel.Notice)]
  [InlineData(0.85, false, ResourceLevel.Act)]
  [InlineData(0.94, false, ResourceLevel.Act)]
  [InlineData(0.95, false, ResourceLevel.Act)]
  [InlineData(0.95, true, ResourceLevel.Emergency)]
  [InlineData(0.99, true, ResourceLevel.Emergency)]
  public void LevelFor_UsageFractionAndTrend_ReturnsExpectedLevel(double fraction, bool rising, ResourceLevel expected)
  {
    long usage = (long)(Budget * fraction);

    Assert.Equal(expected, ResourcePolicy.LevelFor(usage, Budget, rising));
  }

  [Fact]
  public void LevelFor_StableHighWaterMark_IsNotEmergency() =>
    Assert.Equal(ResourceLevel.Act, ResourcePolicy.LevelFor(Budget, Budget, rising: false));

  [Fact]
  public void NextAction_NoAttemptsAtNotice_Steers() =>
    Assert.Equal(ResourceAction.Steer, ResourcePolicy.NextAction(ResourceLevel.Notice, []));

  [Fact]
  public void NextAction_SteerAlreadyAttempted_KillsProgram() =>
    Assert.Equal(ResourceAction.KillProgram,
        ResourcePolicy.NextAction(ResourceLevel.Act, [ResourceAction.Steer]));

  [Fact]
  public void NextAction_SteerAndKillAttempted_ReclaimsMemory() =>
    Assert.Equal(ResourceAction.ReclaimMemory,
        ResourcePolicy.NextAction(ResourceLevel.Act, [ResourceAction.Steer, ResourceAction.KillProgram]));

  [Fact]
  public void NextAction_WholeLadderAttempted_RestartsApp() =>
    Assert.Equal(ResourceAction.RestartApp, ResourcePolicy.NextAction(ResourceLevel.Emergency, ResourcePolicy.Ladder));

  [Fact]
  public void Ladder_IsTheDocumentedOrder() =>
    Assert.Equal(
        [ResourceAction.Steer, ResourceAction.KillProgram, ResourceAction.ReclaimMemory,
         ResourceAction.RestartConversation, ResourceAction.RestartApp],
        ResourcePolicy.Ladder);

  [Fact]
  public void NextAction_NormalLevel_DoesNothing() =>
    Assert.Equal(ResourceAction.None, ResourcePolicy.NextAction(ResourceLevel.Normal, []));
}
