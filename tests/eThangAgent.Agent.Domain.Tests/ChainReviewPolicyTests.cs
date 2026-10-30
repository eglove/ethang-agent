using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>Issue #320: the review decision and its round-trip bound. PURE rule, so this pins
///     the answers without a store, a clock, or a session.</summary>
public class ChainReviewPolicyTests
{
  private static readonly ChainReviewRecord Approve =
      new(ChainReviewVerdict.Approve, []);

  private static ChainReviewRecord Deny(params ChainReviewFinding[] findings) =>
      new(ChainReviewVerdict.Deny, findings);

  private static ChainReviewFinding Finding(string text = "step 3 cites nothing",
      string citation = "3") => new(text, citation);

  [Fact]
  public void Cap_IsThreeRoundTrips_AndTheBoundaryIsSpent()
  {
    // Q14: the review round trip has its OWN counter, cap 3. The boundary is the cap itself,
    // because the count is round trips MADE, not remaining - the ChainRetryPolicy rule.
    Assert.Equal(3, ChainReviewPolicy.MaxReviewAttempts);
    Assert.True(ChainReviewPolicy.CanSendBack(0));
    Assert.True(ChainReviewPolicy.CanSendBack(2));
    Assert.False(ChainReviewPolicy.CanSendBack(3));
    Assert.False(ChainReviewPolicy.CanSendBack(4));
  }

  [Fact]
  public void Decide_WithNoReview_RefusesNamingTheMissingVerdict()
  {
    // THE SILENT DISCARD, CLOSED: a review step that completed without a review is refused,
    // because advancing past it would be the harness ignoring the job the step exists for.
    ChainReviewDecision decision = ChainReviewPolicy.Decide(null, fixTarget: 1, reviewAttempts: 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, decision.Kind);
    Assert.Equal(ChainReviewCode.NoVerdict, decision.Code);
    Assert.Contains("plan.review", decision.Reason, StringComparison.Ordinal);
    Assert.False(decision.IsActionable);
  }

  [Fact]
  public void Decide_OnApprove_Advances()
  {
    ChainReviewDecision decision = ChainReviewPolicy.Decide(Approve, fixTarget: 1, reviewAttempts: 0);

    Assert.Equal(ChainReviewDecisionKind.Advance, decision.Kind);
    Assert.True(decision.IsActionable);
    Assert.Null(decision.Target);
    Assert.Null(decision.ReviewNotes);
  }

  [Fact]
  public void Decide_OnApproveWithConditions_AdvancesCarryingTheFindings()
  {
    // The conditions are findings the planner must address, so they travel the SAME path a
    // denial's notes do - one mechanism for one concept (issue #219's surviving rule).
    ChainReviewRecord conditions = new(ChainReviewVerdict.ApproveWithConditions,
        [Finding("requirement R2 has no step", "R2")]);

    ChainReviewDecision decision = ChainReviewPolicy.Decide(conditions, fixTarget: 1,
        reviewAttempts: 0);

    Assert.Equal(ChainReviewDecisionKind.Advance, decision.Kind);
    Assert.Equal("- requirement R2 has no step [cites: R2]", decision.ReviewNotes);
  }

  [Fact]
  public void Decide_OnDenyUnderTheCap_SendsTheStepBackWithItsFindings()
  {
    ChainReviewDecision decision = ChainReviewPolicy.Decide(
        Deny(Finding("step 3 has no acceptance criterion for requirement R2", "R2")),
        fixTarget: 2, reviewAttempts: 1);

    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(2, decision.Target);
    Assert.Equal("- step 3 has no acceptance criterion for requirement R2 [cites: R2]",
        decision.ReviewNotes);
    Assert.True(decision.IsActionable);
  }

  [Fact]
  public void Decide_OnDenyAtTheCap_ParksNamingTheStep()
  {
    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding()), fixTarget: 2,
        reviewAttempts: ChainReviewPolicy.MaxReviewAttempts);

    Assert.Equal(ChainReviewDecisionKind.ParkAtCap, decision.Kind);
    Assert.Equal(2, decision.Target);
    Assert.Contains("3-round-trip cap", decision.Reason, StringComparison.Ordinal);
    // A cap park is not actionable: the record already carries the reason and a human acts on it.
    Assert.False(decision.IsActionable);
  }

  [Fact]
  public void Decide_OnDenyWithNoFindings_Refuses()
  {
    // 'the reviewer denied' with no notes makes a session guess, which is the failure the rule
    // exists to prevent (issue #219's surviving rule).
    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(), fixTarget: 1, reviewAttempts: 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, decision.Kind);
    Assert.Equal(ChainReviewCode.NoFindings, decision.Code);
  }

  [Fact]
  public void Decide_OnDenyWithNoStepToSendBack_Refuses()
  {
    // A review at the chain's first position judges nothing that can be reopened.
    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding()), fixTarget: null,
        reviewAttempts: 0);

    Assert.Equal(ChainReviewDecisionKind.Refused, decision.Kind);
    Assert.Equal(ChainReviewCode.NoFixTarget, decision.Code);
  }

  [Fact]
  public void Notes_CarryEveryCitation_AndDropMalformedFindings()
  {
    // The citation is what makes a note actionable; a finding that cites nothing is dropped
    // rather than rendered as though it were.
    string? notes = ChainReviewNotes.Render(
        [Finding("first", "1"), new ChainReviewFinding("uncited", "  "), Finding("second", "R4")]);

    Assert.Equal("- first [cites: 1]\n- second [cites: R4]", notes);
  }

  [Fact]
  public void Notes_AreNull_WhenNothingIsRenderable()
  {
    Assert.Null(ChainReviewNotes.Render([]));
    Assert.Null(ChainReviewNotes.Render([new ChainReviewFinding("uncited", "")]));
  }

  [Fact]
  public void Notes_KeepBothOfTwoIdenticalFindings()
  {
    // Records compare by VALUE, so a join that skipped an element equal to the last would lose
    // the separator between two findings that say the same thing.
    string? notes = ChainReviewNotes.Render([Finding("same", "1"), Finding("same", "1")]);

    Assert.Equal("- same [cites: 1]\n- same [cites: 1]", notes);
  }

  [Fact]
  public void Record_IsDenial_OnlyForDeny()
  {
    Assert.True(Deny(Finding()).IsDenial);
    Assert.False(Approve.IsDenial);
    Assert.False(new ChainReviewRecord(ChainReviewVerdict.ApproveWithConditions, []).IsDenial);
  }
}
