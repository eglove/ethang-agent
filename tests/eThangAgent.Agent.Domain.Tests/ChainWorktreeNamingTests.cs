using eThangAgent.AgentDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The name a group member's worktree is provisioned under.</summary>
public class ChainWorktreeNamingTests
{
  [Theory]
  [InlineData(3, 1, "chain-3-step-1")]
  [InlineData(12, 7, "chain-12-step-7")]
  [InlineData(1, 13, "chain-1-step-13")]
  public void For_DerivesAValidWorktreeName(int chainId, int position, string expected)
  {
    string name = ChainWorktreeNaming.For(chainId, position);

    Assert.Equal(expected, name);
    Assert.True(WorktreeName.Create(name).IsSuccess);
  }

  [Fact]
  public void For_IsTheSameNameForTheSameStep() =>
      Assert.Equal(ChainWorktreeNaming.For(9, 4), ChainWorktreeNaming.For(9, 4));

  [Fact]
  public void For_DistinguishesTwoMembersOfOneChain() =>
      Assert.NotEqual(ChainWorktreeNaming.For(1, 1), ChainWorktreeNaming.For(1, 2));

  [Fact]
  public void For_RefusesAChainThatDoesNotExist() =>
      Assert.Throws<ArgumentOutOfRangeException>(() => ChainWorktreeNaming.For(0, 1));

  [Fact]
  public void For_RefusesAStepThatDoesNotExist() =>
      Assert.Throws<ArgumentOutOfRangeException>(() => ChainWorktreeNaming.For(1, 0));
}
