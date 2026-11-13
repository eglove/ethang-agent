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
        new ChainStepSeed(2, ChainStepRole.Refine, Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement));

    Assert.Equal(ChainCreationCode.GroupRefineMember, error!.Code);
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
  public void RefusalFor_AGroupInANonRepository_IsRefused()
  {
    DomainError? error = ChainCreationPolicy.RefusalFor(
        [
            new ChainStepSeed(1, ChainStepRole.Implement, Group: 1),
            new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
            new ChainStepSeed(3, ChainStepRole.Implement),
        ],
        Plan,
        workspaceIsRepository: false);

    Assert.Equal(ChainCreationCode.GroupNeedsRepository, error!.Code);
    // Both remedies, because the refusal is only actionable if it says what to do instead.
    Assert.Contains("git repository", error.Message, StringComparison.Ordinal);
    Assert.Contains("make it one", error.Message, StringComparison.Ordinal);
    Assert.Contains("without a group", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_AGroupInANonRepository_IsRefusedBeforeAnyOtherGroupRule()
  {
    // The declaration is ALSO a group of one, so the ordering is what this pins: nothing
    // else about a group matters if the workspace cannot run it at all.
    DomainError? error = ChainCreationPolicy.RefusalFor(
        [new ChainStepSeed(1, ChainStepRole.Implement, Group: 1)],
        Plan,
        workspaceIsRepository: false);

    Assert.Equal(ChainCreationCode.GroupNeedsRepository, error!.Code);
  }

  [Fact]
  public void RefusalFor_SequentialStepsInANonRepository_Pass()
  {
    // Only a GROUP needs worktrees, so a chain that runs one step at a time is unaffected.
    DomainError? error = ChainCreationPolicy.RefusalFor(
        [ChainStepSeed.Implementing(1), ChainStepSeed.Implementing(2)],
        Plan,
        workspaceIsRepository: false);

    Assert.Null(error);
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
