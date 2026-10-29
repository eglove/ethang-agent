namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #301 D5: the root supervisor's idle clock and the nudge-then-park ladder.
///     Fakes only - the clock is a fact the test supplies, never real elapsed time.</summary>
public class RootSupervisionTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
  private static readonly AgentId Session = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

  [Fact]
  public void CheckIdle_WithNoActivityAtAll_IsNotIdle()
  {
    RootSessionSupervisor supervisor = new(Session);

    // A session that never started is not idle - nudging it would be a message to nobody.
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15), T0));
    Assert.Null(supervisor.IdleAge(T0));
  }

  [Fact]
  public void CheckIdle_BelowTheThreshold_IsNotIdle()
  {
    RootSessionSupervisor supervisor = new(Session);
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnSettled, "turn-settled"));

    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15), T0.AddMinutes(14)));
  }

  [Fact]
  public void CheckIdle_PastTheThreshold_RaisesOncePerBreach()
  {
    RootSessionSupervisor supervisor = new(Session);
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnSettled, "turn-settled"));
    DateTimeOffset late = T0.AddMinutes(20);

    RootIdleAlert? alert = supervisor.CheckIdle(TimeSpan.FromMinutes(15), late);
    Assert.NotNull(alert);
    Assert.Equal(Session, alert.SessionId);

    // A tick that re-raised every interval would spend the whole budget on one stall.
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15), late.AddMinutes(1)));
  }

  [Fact]
  public void OnActivity_AfterABreach_RearmsTheAlert()
  {
    RootSessionSupervisor supervisor = new(Session);
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnSettled, "turn-settled"));
    DateTimeOffset late = T0.AddMinutes(20);
    Assert.NotNull(supervisor.CheckIdle(TimeSpan.FromMinutes(15), late));

    supervisor.OnActivity(new RootTurnActivity(Session, late, RootTurnPhase.ToolStarted, "tool:exec"));

    Assert.NotNull(supervisor.CheckIdle(TimeSpan.FromMinutes(15), late.AddMinutes(20)));
  }

  [Fact]
  public void OnActivity_WithAWorkFact_ResetsTheIdleClock()
  {
    RootSessionSupervisor supervisor = new(Session);
    supervisor.OnActivity(new RootTurnActivity(Session, T0, RootTurnPhase.TurnSettled, "turn-settled"));

    // A tool dispatch inside the turn is WORK: the clock restarts from it.
    supervisor.OnActivity(new RootTurnActivity(Session, T0.AddMinutes(10), RootTurnPhase.ToolStarted, "tool:exec"));

    Assert.Equal(TimeSpan.FromMinutes(4), supervisor.IdleAge(T0.AddMinutes(14)));
    Assert.Null(supervisor.CheckIdle(TimeSpan.FromMinutes(15), T0.AddMinutes(14)));
  }

  [Fact]
  public void Decide_SpendsTheNudgeBudgetThenParks()
  {
    Assert.Equal(RootSupervisionDecision.Nudge, RootSupervisionPolicy.Decide(0, 1));
    Assert.Equal(RootSupervisionDecision.Park, RootSupervisionPolicy.Decide(1, 1));
    Assert.Equal(RootSupervisionDecision.Park, RootSupervisionPolicy.Decide(2, 1));
  }

  [Fact]
  public void Decide_WithAZeroBudget_ParksImmediately()
      => Assert.Equal(RootSupervisionDecision.Park, RootSupervisionPolicy.Decide(0, 0));

  [Fact]
  public void Nudge_NamesTheStepAndTheChain_AndIsNotTheWatchdogWrapUpText()
  {
    string nudge = RootSupervisionPolicy.Nudge(chainId: 4, position: 2);

    Assert.Contains("chain #4", nudge, StringComparison.Ordinal);
    Assert.Contains("step 2", nudge, StringComparison.Ordinal);
    Assert.Contains("Done", nudge, StringComparison.Ordinal);

    // The watchdog's nudge asks for a REPORT; a chain step owes a COMPLETION. Reusing it
    // would tell the session to produce exactly the wrong artifact.
    Assert.NotEqual(WatchdogPolicy.WrapUpNudge(15), nudge);
    Assert.DoesNotContain("final report", nudge, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void RecordWrapUp_CountsAttempts()
  {
    RootSessionSupervisor supervisor = new(Session);
    Assert.Equal(0, supervisor.WrapUpAttempts);

    supervisor.RecordWrapUp();
    supervisor.RecordWrapUp();

    Assert.Equal(2, supervisor.WrapUpAttempts);
  }
}
