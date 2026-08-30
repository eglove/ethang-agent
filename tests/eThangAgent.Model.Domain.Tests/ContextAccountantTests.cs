using eThangAgent.ModelDomain;

namespace eThangAgent.Model.Domain.Tests;

public class ContextAccountantTests
{
  [Fact]
  public void BeforeFirstReport_StatusZeroed_BreakdownNull()
  {
    ContextAccountant accountant = new(1000);

    ContextStatus status = accountant.Status;
    Assert.Null(status.LastInputTokens);
    Assert.Equal(0, status.TotalInputTokens);
    Assert.Equal(0, status.TotalOutputTokens);
    Assert.Equal(1000, status.ContextWindow);
    Assert.Null(status.UtilizationPercent);
    Assert.Null(accountant.Breakdown);
  }

  [Fact]
  public void OnRequestUsage_Accumulates_AndComputesUtilization()
  {
    ContextAccountant accountant = new(1000);
    ContextComposition composition = new(100, 700, 200);

    accountant.OnRequestUsage(new TokenUsage(800, 50), composition);
    accountant.OnRequestUsage(new TokenUsage(900, 60), composition);

    ContextStatus status = accountant.Status;
    Assert.Equal(900, status.LastInputTokens);
    Assert.Equal(1700, status.TotalInputTokens);
    Assert.Equal(110, status.TotalOutputTokens);
    Assert.Equal(90.0, status.UtilizationPercent);
  }

  [Fact]
  public void NullWindow_UtilizationStaysNull()
  {
    ContextAccountant accountant = new(null);
    accountant.OnRequestUsage(new TokenUsage(800, 50), new ContextComposition(1, 1, 1));

    Assert.Null(accountant.Status.UtilizationPercent);
  }

  [Fact]
  public void Breakdown_ScalesCharacterSharesAgainstLastInput()
  {
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(1000, 10), new ContextComposition(100, 700, 200));

    ContextBreakdown breakdown = accountant.Breakdown!;
    Assert.Equal(100, breakdown.SystemPromptTokens);
    Assert.Equal(700, breakdown.MessageTokens);
    Assert.Equal(200, breakdown.ToolTokens);
  }

  [Fact]
  public void Breakdown_ZeroComposition_AllBucketsNull()
  {
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(500, 10), new ContextComposition(0, 0, 0));

    ContextBreakdown breakdown = accountant.Breakdown!;
    Assert.Null(breakdown.SystemPromptTokens);
    Assert.Null(breakdown.MessageTokens);
    Assert.Null(breakdown.ToolTokens);
  }

  [Fact]
  public void ThresholdBoundary_ExactlyEightyPercent_IsReached()
  {
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(800, 10), new ContextComposition(1, 1, 1));

    Assert.Equal(80.0, accountant.Status.UtilizationPercent);
  }
}
