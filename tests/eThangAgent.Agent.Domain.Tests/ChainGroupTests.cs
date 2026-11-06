using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>A step's group on the chain record: the members, the barrier they form, and the
///     cursor that refuses to offer work past an unsettled group.</summary>
public class ChainGroupTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

  private static Chain Grouped() => Chain.NewFromSeeds("ws", 3, 1, "t", "session-root",
      [
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement),
      ], T0);

  [Fact]
  public void GroupMembers_ReturnsTheMembersInPositionOrder()
  {
    Chain chain = Grouped();

    Assert.Equal([1, 2], chain.GroupMembers(1).Select(step => step.Position));
    Assert.Equal(1, chain.GroupAt(2));
    Assert.Null(chain.GroupAt(3));
  }

  [Fact]
  public void GroupIsSettled_IsFalseWhileAMemberIsPending() =>
      Assert.False(Grouped().GroupIsSettled(1));

  [Fact]
  public void GroupIsSettled_IsTrueWhenEveryMemberIsDone()
  {
    Chain chain = Grouped()
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-b"), null, T0);

    Assert.True(chain.GroupIsSettled(1));
  }

  [Fact]
  public void NextAvailableStep_DoesNotOfferAStepAfterAnUnsettledGroup()
  {
    Chain chain = Grouped().ClaimStep(1, "session-a", T0);

    Assert.Equal(2, chain.NextAvailableStep()!.Position);
  }
}
