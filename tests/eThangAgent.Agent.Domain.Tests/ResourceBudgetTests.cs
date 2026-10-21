using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

public sealed class ResourceBudgetTests
{
  [Fact]
  public void Constructor_PhysicalMemory_DerivesHalfAsTotal()
  {
    ResourceBudget budget = new(16_000_000_000L, 1);

    Assert.Equal(8_000_000_000L, budget.TotalBytes);
  }

  [Fact]
  public void Constructor_TwoOpenConversations_SplitsTotalEvenly()
  {
    ResourceBudget budget = new(16_000_000_000L, 2);

    Assert.Equal(4_000_000_000L, budget.PerConversationBytes);
  }

  [Fact]
  public void Constructor_AnyConversationCount_PerSpawnIsQuarterOfTotal()
  {
    ResourceBudget budget = new(16_000_000_000L, 3);

    Assert.Equal(2_000_000_000L, budget.PerSpawnBytes);
  }

  [Fact]
  public void Constructor_ZeroConversations_Throws() =>
    _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceBudget(16_000_000_000L, 0));

  [Fact]
  public void Constructor_NonPositivePhysicalMemory_Throws() =>
    _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceBudget(0L, 1));
}
