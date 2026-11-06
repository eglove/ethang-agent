using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>A member's completion is refused while its worktree holds uncommitted changes:
///     the reconciler merges branches, so work that was never committed cannot land.</summary>
public class ChainWorktreeCommitPolicyTests
{
  private const string Worktree = "C:\\ws\\.worktrees\\chain-1-step-2";

  [Fact]
  public void RefusalFor_AStepWithNoWorktree_IsNotRefused()
  {
    DomainError? refusal = ChainWorktreeCommitPolicy.RefusalFor(ChainStep.Pending(1),
        worktreeIsDirty: true);

    Assert.Null(refusal);
  }

  [Fact]
  public void RefusalFor_ACleanWorktree_IsNotRefused()
  {
    DomainError? refusal = ChainWorktreeCommitPolicy.RefusalFor(Step(), worktreeIsDirty: false);

    Assert.Null(refusal);
  }

  [Fact]
  public void RefusalFor_ADirtyWorktree_IsRefusedByCode()
  {
    DomainError? refusal = ChainWorktreeCommitPolicy.RefusalFor(Step(), worktreeIsDirty: true);

    Assert.NotNull(refusal);
    Assert.Equal(ChainWorktreeCommitCode.WorktreeDirty, refusal.Code);
    Assert.Equal("ChainWorktreeDirty", refusal.Code);
  }

  [Fact]
  public void RefusalFor_ADirtyWorktree_NamesThePathAndBothCommands()
  {
    DomainError? refusal = ChainWorktreeCommitPolicy.RefusalFor(Step(), worktreeIsDirty: true);

    Assert.NotNull(refusal);
    Assert.Contains(Worktree, refusal.Message, StringComparison.Ordinal);
    Assert.Contains("git add -A", refusal.Message, StringComparison.Ordinal);
    Assert.Contains("git commit -m", refusal.Message, StringComparison.Ordinal);
    Assert.Contains("step 2", refusal.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusalFor_ADirtyWorktree_ReadsAsANudge()
  {
    DomainError? refusal = ChainWorktreeCommitPolicy.RefusalFor(Step(), worktreeIsDirty: true);

    Assert.NotNull(refusal);
    Assert.Equal(
        "chain step 2 is finished, but its worktree at " + Worktree + " has uncommitted changes. "
        + "The harness merges your branch, so your work must be committed. "
        + "Run `git add -A` and `git commit -m \"<what you did>\"` in that worktree, "
        + "then mark the step done again.",
        refusal.Message);
  }

  private static ChainStep Step() =>
      ChainStep.Pending(2, ChainStepRole.Implement, group: 1).WithWorktree(Worktree, "chain-1-step-2");
}
