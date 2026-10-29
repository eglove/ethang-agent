namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #301: the pure advance rule. Fakes only - no store, no clock, no runtime -
///     so every branch of 'what happens after a completion fact' is pinned directly.</summary>
public class ChainAdvancePolicyTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  private static Chain NewChain(params int[] positions) => Chain.New("C:\\ws", 3, 1, "t",
      "session-root", positions.Length == 0 ? [1] : positions, T0);

  /// <summary>A chain whose step at <paramref name="position"/> is claimed by a session.</summary>
  private static Chain Claimed(Chain chain, int position, string sessionId, DateTimeOffset at)
      => chain.ClaimStep(position, sessionId, at);

  [Fact]
  public void Decide_OnTheAssignedStepDone_StartsTheNextStep()
  {
    Chain chain = Claimed(NewChain(1, 2), 1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0);

    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 1, "session-a", T0);

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
    Assert.True(decision.IsActionable);
  }

  [Fact]
  public void Decide_OnTheLastStepDone_CompletesTheChain()
  {
    Chain chain = Claimed(NewChain(1), 1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0);

    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 1, "session-a", T0);

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
  }

  [Fact]
  public void Decide_OnAForeignStep_RefusesAndNamesTheAssignedSession()
  {
    Chain chain = Claimed(NewChain(1, 2), 2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);

    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 2, "session-a", T0);

    Assert.Equal(ChainAdvanceKind.Refused, decision.Kind);
    Assert.Equal(ChainAdvanceCode.ForeignStep, decision.Code);
    Assert.Contains("session-b", decision.Reason, StringComparison.Ordinal);
    Assert.Contains("session-a", decision.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Decide_OnAnUnknownStep_RefusesNamingTheStepAndTheCount()
  {
    ChainAdvance decision = ChainAdvancePolicy.Decide(NewChain(1), 9, "session-a", T0);

    Assert.Equal(ChainAdvanceCode.UnknownStep, decision.Code);
    Assert.Contains("9", decision.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Decide_OnAStepThatIsNotDone_RefusesRatherThanAdvancingAnIntention()
  {
    Chain chain = Claimed(NewChain(1, 2), 1, "session-a", T0);

    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 1, "session-a", T0);

    Assert.Equal(ChainAdvanceCode.StepNotDone, decision.Code);
  }

  [Fact]
  public void Decide_OnALapsedClaim_RefusesWithItsOwnCode()
  {
    // Claimed an hour and a minute ago: past the one-hour window.
    DateTimeOffset stale = T0 - ChainClaimPolicy.ClaimTtl - TimeSpan.FromMinutes(1);
    Chain chain = Claimed(NewChain(1, 2), 1, "session-a", stale);

    ChainAdvance decision = ChainAdvancePolicy.Decide(chain, 1, "session-a", T0);

    Assert.Equal(ChainAdvanceCode.ClaimLapsed, decision.Code);
    Assert.Contains("re-claim", decision.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Next_WithNothingAvailableAndAStepUnsettled_IsBlockedNamingIt()
  {
    // The only step is parked, so there is nothing to start and nothing to complete.
    // 'Nothing happened' and 'waiting on step 1' must not look alike (#303 D8).
    Chain chain = Claimed(NewChain(1), 1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Parked, ChainActor.Session("session-a"), "waiting", T0);

    ChainAdvance decision = ChainAdvancePolicy.Next(chain, T0);

    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Assert.Equal(ChainAdvanceCode.StepsUnsettled, decision.Code);
    Assert.Contains("1 (Parked)", decision.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Next_WithAParkedStepAndAPendingOne_StartsThePendingOne()
  {
    // Park-and-continue (#303 D4) is the aggregate's own rule: a parked step does not block
    // an INDEPENDENT one, and #301 reads the cursor rather than re-deciding it.
    Chain chain = Claimed(NewChain(1, 2), 1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Parked, ChainActor.Session("session-a"), "waiting", T0);

    ChainAdvance decision = ChainAdvancePolicy.Next(chain, T0);

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
  }

  [Fact]
  public void Next_WithASkippedStep_CompletesTheChain()
  {
    Chain chain = Claimed(NewChain(1), 1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Skipped, ChainActor.Session("session-a"), "not doing", T0);

    Assert.Equal(ChainAdvanceKind.CompleteChain, ChainAdvancePolicy.Next(chain, T0).Kind);
  }
}
