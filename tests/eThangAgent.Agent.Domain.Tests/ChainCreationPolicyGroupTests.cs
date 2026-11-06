using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A group is a barrier around parallel work, so a declaration that cannot form one is
///     refused at creation rather than parked once its members have already spent sessions.
///     PURE rules: no store, no clock.</summary>
public class ChainCreationPolicyGroupTests
{
  private static readonly IReadOnlyList<int> Plan = [1, 2, 3, 4];

  private static DomainError? Refusal(params ChainStepSeed[] seeds) =>
      ChainCreationPolicy.RefusalFor(seeds, Plan);

  private static DomainError? RefusalIn(IReadOnlyList<int> plan, params ChainStepSeed[] seeds) =>
      ChainCreationPolicy.RefusalFor(seeds, plan);

  [Fact]
  public void RefusalFor_AGroupOfOne_IsRefused()
  {
    DomainError? error = Refusal(new ChainStepSeed(1, ChainStepRole.Implement, Group: 1));

    Assert.Equal(ChainCreationCode.GroupTooSmall, error!.Code);
  }

  [Fact]
  public void RefusalFor_AGroupWithAReviewMember_IsRefused()
  {
    DomainError? error = Refusal(
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Review("vendor/r"), Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement));

    Assert.Equal(ChainCreationCode.GroupReviewMember, error!.Code);
  }

  [Fact]
  public void RefusalFor_AGroupWithNoReconciler_IsRefused()
  {
    DomainError? error = Refusal(
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1));

    Assert.Equal(ChainCreationCode.GroupWithoutReconciler, error!.Code);
  }

  [Fact]
  public void RefusalFor_AReconcilerThatIsItselfAGroupMember_IsRefused()
  {
    // Group 1 ends at step 2, so step 3 is its reconciler slot - and step 3 belongs to
    // group 2, so the step that would merge group 1 starts with its own members.
    DomainError? error = RefusalIn([1, 2, 3, 4, 5],
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement, Group: 2),
        new ChainStepSeed(4, ChainStepRole.Implement, Group: 2),
        new ChainStepSeed(5, ChainStepRole.Implement));

    Assert.Equal(ChainCreationCode.GroupReconcilerNotAdjacent, error!.Code);
  }

  [Fact]
  public void RefusalFor_AGroupWithAnAdjacentReconciler_Passes()
  {
    DomainError? error = Refusal(
        new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement));

    Assert.Null(error);
  }
}
