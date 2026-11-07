using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>D11: a review after a group judges the merged result, and a denial reopens the
///     member whose position a finding cites rather than the reconciler that merged it.
///     <para>PURE: the chain record supplies the group's members and the nearest preceding Done
///     step, the policy answers which position a denial reopens.</para></summary>
public class ChainReviewCitationRoutingTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

  private static ChainReviewRecord Deny(params ChainReviewFinding[] findings) =>
      new(ChainReviewVerdict.Deny, findings);

  private static ChainReviewFinding Finding(string citation) =>
      new("the merged result does not do what the step promised", citation);

  /// <summary>A chain of implement(1), implement(2) in group 1, reconciler(3), review(4), with
  ///     the members and the reconciler already Done.</summary>
  private static Chain GroupedAndLanded() =>
      Chain.NewFromSeeds("ws", 3, 1, "t", "session-root",
          [
            new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
            new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
            ChainStepSeed.Implementing(3),
            new ChainStepSeed(4, ChainStepRole.Review("vendor/reviewer")),
          ], T0)
      .ClaimStep(1, "session-a", T0)
      .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
      .ClaimStep(2, "session-b", T0)
      .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0)
      .ClaimStep(3, "session-c", T0)
      .SettleStep(3, ChainStepStatus.Done, ChainActor.Session("session-c"), null, T0);

  /// <summary>The same positions with no group at all: the sequential chain a review judges
  ///     today.</summary>
  private static Chain Sequential() =>
      Chain.NewFromSeeds("ws", 3, 1, "t", "session-root",
          [
            ChainStepSeed.Implementing(1),
            ChainStepSeed.Implementing(2),
            ChainStepSeed.Implementing(3),
            new ChainStepSeed(4, ChainStepRole.Review("vendor/reviewer")),
          ], T0)
      .ClaimStep(1, "session-a", T0)
      .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
      .ClaimStep(2, "session-b", T0)
      .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0)
      .ClaimStep(3, "session-c", T0)
      .SettleStep(3, ChainStepStatus.Done, ChainActor.Session("session-c"), null, T0);

  /// <summary>The member positions the advance derives for a review step: the members of the
  ///     group whose reconciler is the step the review judges, or null when that step is not a
  ///     reconciler.
  ///     <para>THE GROUP IS READ ONE POSITION FURTHER BACK than the reconciler, because the
  ///     reconciler is not itself a member - chain creation refuses a reconciler that is.</para>
  ///     </summary>
  private static IReadOnlyList<int>? MembersBefore(Chain chain, int reviewPosition) =>
      chain.FixTargetFor(reviewPosition) is { } reconciler
          && chain.GroupAt(reconciler.Position - 1) is { } group
              ? [.. chain.GroupMembers(group).Select(step => step.Position)]
              : null;

  [Fact]
  public void Decide_OnDenyCitingAMember_ReopensThatMember()
  {
    // THE DEFECT THIS CLOSES: the nearest preceding Done step is the RECONCILER, so a denial
    // about a member's work would send the reconciler back to merge branches nobody changed.
    Chain chain = GroupedAndLanded();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("1")),
        chain.FixTargetFor(4)?.Position, reviewAttempts: 0, MembersBefore(chain, 4));

    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(1, decision.Target);
  }

  [Fact]
  public void Decide_OnDenyCitingTheReconciler_ReopensTheReconciler()
  {
    // A finding about the merge itself is the reconciler's work, so the citation routes there.
    Chain chain = GroupedAndLanded();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("3")),
        chain.FixTargetFor(4)?.Position, reviewAttempts: 0, MembersBefore(chain, 4));

    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(3, decision.Target);
  }

  [Fact]
  public void Decide_OnDenyCitingNoStep_ReopensTheNearestPrecedingStep()
  {
    // A citation naming a requirement is not a position, so it routes nowhere and the
    // sequential rule stands: the step the review actually judged.
    Chain chain = GroupedAndLanded();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("R2")),
        chain.FixTargetFor(4)?.Position, reviewAttempts: 0, MembersBefore(chain, 4));

    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(3, decision.Target);
  }

  [Fact]
  public void Decide_OnAChainWithNoGroup_KeepsTheNearestPrecedingStep()
  {
    // No group means no member to route to, whatever the citation says.
    Chain chain = Sequential();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("1")),
        chain.FixTargetFor(4)?.Position, reviewAttempts: 0, MembersBefore(chain, 4));

    Assert.Null(MembersBefore(chain, 4));
    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(3, decision.Target);
  }

  [Fact]
  public void Decide_OnDenyCitingAStepThatIsNotAMember_KeepsTheNearestPrecedingStep()
  {
    // Position 2 is a member here, but a citation naming a position outside the group is not
    // the group's routing information - it is a citation the chain cannot honour, so the
    // denial goes where it would without one.
    Chain chain = GroupedAndLanded();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("9")),
        chain.FixTargetFor(4)?.Position, reviewAttempts: 0, MembersBefore(chain, 4));

    Assert.Equal(ChainReviewDecisionKind.SendBack, decision.Kind);
    Assert.Equal(3, decision.Target);
  }

  [Fact]
  public void Decide_OnDenyCitingAMemberAtTheCap_ParksNamingThatMember()
  {
    // The routing decides WHICH step the round-trip budget belongs to, so a cap park names
    // the member the reviewer keeps rejecting rather than the reconciler.
    Chain chain = GroupedAndLanded();

    ChainReviewDecision decision = ChainReviewPolicy.Decide(Deny(Finding("1")),
        chain.FixTargetFor(4)?.Position, ChainReviewPolicy.MaxReviewAttempts, MembersBefore(chain, 4));

    Assert.Equal(ChainReviewDecisionKind.ParkAtCap, decision.Kind);
    Assert.Equal(1, decision.Target);
    Assert.Contains("step 1", decision.Reason, StringComparison.Ordinal);
  }
}
