namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #300: the chain aggregate's status machine, claim TTL, and freeze rules,
///     exercised against the domain alone (no store, no database).</summary>
public class ChainAggregateTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

  private static Chain NewChain(params int[] positions)
      => Chain.New("C:\\ws\\alpha", 7, 3, "Ship it", "session-a", positions.Length == 0 ? [1] : positions, T0);

  [Fact]
  public void New_IsRunning_VersionZero_IdUnassigned_WithPendingSteps()
  {
    Chain chain = NewChain(1, 2, 3);
    Assert.Equal(ChainStatus.Running, chain.Status);
    Assert.Equal(0, chain.Version);
    Assert.Equal(0, chain.Id);
    Assert.Equal([1, 2, 3], chain.Steps.Select(s => s.Position));
    Assert.All(chain.Steps, s => Assert.Equal(ChainStepStatus.Pending, s.Status));
    Assert.All(chain.Steps, s => Assert.Equal(0, s.Attempts));
    Assert.Equal(7, chain.PlanId);
    Assert.Equal(3, chain.PlanVersion);
  }

  [Fact]
  public void New_OrdersAndDeduplicatesPositions()
  {
    Chain chain = NewChain(3, 1, 3, 2);
    Assert.Equal([1, 2, 3], chain.Steps.Select(s => s.Position));
  }

  [Fact]
  public void New_WithNoStepPositions_Throws()
  {
    ChainInputException ex = Assert.Throws<ChainInputException>(
        () => Chain.New("C:\\ws", 1, 1, "t", null, [], T0));
    Assert.Contains("at least one executable step", ex.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void New_RejectsEmptyTitle(string title)
      => Assert.Throws<ArgumentException>(() => Chain.New("C:\\ws", 1, 1, title, null, [1], T0));

  [Fact]
  public void New_RejectsNonPositivePlanId()
      => Assert.Throws<ArgumentOutOfRangeException>(() => Chain.New("C:\\ws", 0, 1, "t", null, [1], T0));

  // ---- status machine ------------------------------------------------------

  [Fact]
  public void SetStatus_RunningToCompleted_WithSettledSteps_Succeeds()
  {
    Chain chain = NewChain(1)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, "ok", T0);
    Chain done = chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);
    Assert.Equal(ChainStatus.Completed, done.Status);
  }

  [Fact]
  public void SetStatus_Completed_WithPendingStep_Throws_NamingPositionAndStatus()
  {
    Chain chain = NewChain(1, 2);
    ChainInputException ex = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));
    Assert.Contains("1 (Pending)", ex.Message, StringComparison.Ordinal);
    Assert.Contains("2 (Pending)", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void SetStatus_Completed_WithParkedStep_Throws()
  {
    Chain chain = NewChain(1)
        .SettleStep(1, ChainStepStatus.Parked, ChainActor.Harness, "waiting", T0);
    _ = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));
  }

  [Fact]
  public void SetStatus_Completed_WithSkippedStep_Succeeds()
  {
    Chain chain = NewChain(1)
        .SettleStep(1, ChainStepStatus.Skipped, ChainActor.Harness, "not doing it", T0);
    Assert.Equal(ChainStatus.Completed,
        chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0).Status);
  }

  [Fact]
  public void SetStatus_Abandoned_WithPendingSteps_StillSucceeds()
  {
    Chain chain = NewChain(1, 2);
    Assert.Equal(ChainStatus.Abandoned,
        chain.SetStatus(ChainStatus.Abandoned, ChainActor.User, T0).Status);
  }

  [Fact]
  public void SetStatus_FromTerminal_Throws()
  {
    Chain completed = NewChain(1)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);
    _ = Assert.Throws<ChainInputException>(
        () => completed.SetStatus(ChainStatus.Abandoned, ChainActor.User, T0));
  }

  [Fact]
  public void SetStatus_ToItsOwnStatus_Throws()
  {
    Chain chain = NewChain(1);
    _ = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Running, ChainActor.Harness, T0));
  }

  [Fact]
  public void Park_RecordsReasonAndStep_AndResumeClearsThem()
  {
    Chain parked = NewChain(1, 2).Park("reviewer denied twice", 2, ChainActor.Harness, T0);
    Assert.Equal(ChainStatus.Parked, parked.Status);
    Assert.Equal("reviewer denied twice", parked.ParkReason);
    Assert.Equal(2, parked.ParkedAtPosition);

    Chain resumed = parked.Resume(ChainActor.User, T0);
    Assert.Equal(ChainStatus.Running, resumed.Status);
    Assert.Null(resumed.ParkReason);
    Assert.Null(resumed.ParkedAtPosition);
  }

  [Fact]
  public void Park_WithUnknownStepPosition_Throws()
      => Assert.Throws<ChainInputException>(() => NewChain(1).Park("why", 9, ChainActor.Harness, T0));

  [Fact]
  public void Park_WithEmptyReason_Throws()
      => Assert.Throws<ArgumentException>(() => NewChain(1).Park("  ", null, ChainActor.Harness, T0));

  [Fact]
  public void TransitionSpecification_RejectsTerminalMoves()
  {
    Chain done = NewChain(1)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);
    Assert.False(new ChainStatusTransition(ChainStatus.Running).IsSatisfiedBy(done));
    Assert.False(new ChainStatusTransition(ChainStatus.Completed).IsSatisfiedBy(done));
  }

  [Fact]
  public void TransitionSpecification_AllowsParkedToRunning()
  {
    Chain parked = NewChain(1).Park("why", 1, ChainActor.Harness, T0);
    Assert.True(new ChainStatusTransition(ChainStatus.Running).IsSatisfiedBy(parked));
  }

  // ---- claims --------------------------------------------------------------

  [Fact]
  public void ClaimStep_MarksRunning_IncrementsAttempts_AndStampsSession()
  {
    Chain chain = NewChain(1).ClaimStep(1, "session-a", T0);
    ChainStep step = chain.Steps[0];
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.Equal(1, step.Attempts);
    Assert.Equal("session-a", step.SessionId);
    Assert.Equal(T0, step.ClaimedAt);
    Assert.Equal("session:session-a", step.Actor.Encoded);
  }

  [Fact]
  public void ClaimStep_WhileAnotherClaimIsHeld_Throws()
  {
    Chain chain = NewChain(1).ClaimStep(1, "session-a", T0);
    ChainInputException ex = Assert.Throws<ChainInputException>(
        () => chain.ClaimStep(1, "session-b", T0.AddMinutes(5)));
    Assert.Contains("Running", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ClaimStep_AfterAnyAmountOfTime_IsStillRefused_AClaimNeverExpires()
  {
    // A chain step has NO time limit: a claim held for a year is as live as one held for a
    // second, and only settling, releasing, or reassigning it frees the step.
    Chain chain = NewChain(1).ClaimStep(1, "session-a", T0);
    DateTimeOffset muchLater = T0.AddYears(1);

    ChainStep step = chain.Steps[0];
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.False(step.IsAvailable);
    Assert.False(chain.IsSettled());
    _ = Assert.Throws<ChainInputException>(() => chain.ClaimStep(1, "session-b", muchLater));
  }

  [Fact]
  public void ReassignStep_MovesAHeldClaimWithoutSpendingAnAttempt()
  {
    Chain chain = NewChain(1).ClaimStep(1, "session-a", T0).ReassignStep(1, "session-b", T0);
    Assert.Equal("session-b", chain.Steps[0].SessionId);
    Assert.Equal(1, chain.Steps[0].Attempts);
  }

  [Fact]
  public void ReassignStep_OnAPendingStep_Throws()
  {
    Chain chain = NewChain(1);
    _ = Assert.Throws<ChainInputException>(() => chain.ReassignStep(1, "session-b", T0));
  }

  [Fact]
  public void NextAvailableStep_SkipsSettledSteps()
  {
    Chain chain = NewChain(1, 2)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0);
    Assert.Equal(2, chain.NextAvailableStep()!.Position);
  }

  [Fact]
  public void NextAvailableStep_WhenEverythingIsSettled_IsNull()
  {
    Chain chain = NewChain(1)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0);
    Assert.Null(chain.NextAvailableStep());
    Assert.True(chain.IsSettled());
  }

  [Fact]
  public void IsSettled_IsFalseWhileAStepIsHeldByAClaim()
  {
    Chain chain = NewChain(1).ClaimStep(1, "session-a", T0);
    Assert.False(chain.IsSettled());
  }

  // ---- settle --------------------------------------------------------------

  [Fact]
  public void SettleStep_RecordsOutcomeActorAndCompletionTime()
  {
    Chain chain = NewChain(1)
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), "gate green", T0.AddMinutes(5));
    ChainStep step = chain.Steps[0];
    Assert.Equal(ChainStepStatus.Done, step.Status);
    Assert.Equal("gate green", step.Outcome);
    Assert.Equal(T0.AddMinutes(5), step.CompletedAt);
    Assert.Null(step.ClaimedAt);
    Assert.Equal("session:session-a", step.Actor.Encoded);
  }

  [Theory]
  [InlineData(ChainStepStatus.Pending)]
  [InlineData(ChainStepStatus.Running)]
  public void SettleStep_WithANonSettledStatus_Throws(ChainStepStatus status)
  {
    Chain chain = NewChain(1);
    _ = Assert.Throws<ArgumentException>(
        () => chain.SettleStep(1, status, ChainActor.Harness, null, T0));
  }

  [Fact]
  public void SettleStep_AtAnUnknownPosition_Throws()
      => Assert.Throws<ChainInputException>(
          () => NewChain(1).SettleStep(9, ChainStepStatus.Done, ChainActor.Harness, null, T0));

  [Fact]
  public void TerminalChain_IsFrozen_ForStepMutations()
  {
    Chain done = NewChain(1)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);
    _ = Assert.Throws<ChainInputException>(() => done.ClaimStep(1, "s", T0));
    _ = Assert.Throws<ChainInputException>(
        () => done.SettleStep(1, ChainStepStatus.Done, ChainActor.Harness, null, T0));
  }
}
