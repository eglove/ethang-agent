namespace eThangAgent.AgentDomain.Tests;

public class ChainAdvancePolicyGroupTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

  private static Chain Grouped() => Chain.NewFromSeeds("ws", 3, 1, "t", "session-root",
      [new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
       new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
       new ChainStepSeed(3, ChainStepRole.Implement)], T0);

  [Fact]
  public void Next_WithAFreshGroup_StartsTheFirstMember()
  {
    ChainAdvance decision = ChainAdvancePolicy.Next(Grouped());
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(1, decision.Position);
  }

  [Fact]
  public void Next_WithOneMemberStillPending_StartsThatMember()
  {
    ChainAdvance decision = ChainAdvancePolicy.Next(Grouped().ClaimStep(1, "session-a", T0));
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
  }

  [Fact]
  public void Next_WithTheGroupSettled_StartsTheStepAfterIt()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(3, decision.Position);
  }

  [Fact]
  public void Next_WithAParkedMemberAndTheRestDone_IsBlockedNotComplete()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Parked, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.NotEqual(ChainAdvanceKind.CompleteChain, decision.Kind);
  }

  [Fact]
  public void Next_WithAParkedMemberAndTheRestDone_OffersTheStepAfterTheGroup()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Parked, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(3, decision.Position);
  }

  [Fact]
  public void Next_WhileTheGroupIsStillRunning_IsBlocked()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .ClaimStep(2, "session-b", T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Assert.Equal(ChainAdvanceCode.StepsUnsettled, decision.Code);
  }

  [Fact]
  public void Next_DoesNotOfferAStepOfALaterGroupWhileAnEarlierGroupIsUnsettled()
  {
    Chain chain = Chain.NewFromSeeds("ws", 4, 1, "t", "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
         new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
         new ChainStepSeed(3, ChainStepRole.Implement, Group: 2),
         new ChainStepSeed(4, ChainStepRole.Implement, Group: 2)], T0)
        .ClaimStep(1, "session-a", T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
  }

  [Fact]
  public void Next_DoesNotOfferAStandaloneStepWhileAGroupIsUnsettled()
  {
    Chain chain = Chain.NewFromSeeds("ws", 3, 1, "t", "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
         new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
         new ChainStepSeed(3, ChainStepRole.Implement)], T0)
        .ClaimStep(1, "session-a", T0)
        .ClaimStep(2, "session-b", T0);
    ChainAdvance decision = ChainAdvancePolicy.Next(chain);
    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
  }

  [Fact]
  public void Decide_WhenOneMemberCompletes_StartsItsSiblingNotTheStepAfterTheGroup()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0);
    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 1, "session-a");
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
  }

  [Fact]
  public void Decide_WhenTheLastMemberCompletes_StartsTheStepAfterTheGroup()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);
    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 2, "session-b");
    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(3, decision.Position);
  }
}
