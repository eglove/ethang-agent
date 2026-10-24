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
  public void CacheAndReasoning_AccumulateAcrossCalls_AndKeepTheLastValue()
  {
    ContextAccountant accountant = new(1000);
    ContextComposition composition = new(1, 1, 1);

    accountant.OnRequestUsage(new TokenUsage(500, 20, 400, null, 15), composition);
    accountant.OnRequestUsage(new TokenUsage(600, 30, 500, null, 25), composition);

    ContextStatus status = accountant.Status;
    Assert.Equal(500, status.LastCachedInputTokens);
    Assert.Equal(900, status.TotalCachedInputTokens);
    Assert.Equal(25, status.LastReasoningTokens);
    Assert.Equal(40, status.TotalReasoningTokens);
    Assert.Equal(83.33, status.CacheHitPercent);
  }

  [Fact]
  public void UnreportedCounts_StayNull_AndTotalsAddNothing()
  {
    // A provider that reports no cache and no reasoning must not be recorded as
    // measured-cold: the last values stay null and the totals stay zero.
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(500, 20, 400, null, 15), new ContextComposition(1, 1, 1));
    accountant.OnRequestUsage(new TokenUsage(600, 30), new ContextComposition(1, 1, 1));

    ContextStatus status = accountant.Status;
    Assert.Null(status.LastCachedInputTokens);
    Assert.Null(status.LastReasoningTokens);
    Assert.Null(status.CacheHitPercent);
    Assert.Equal(400, status.TotalCachedInputTokens);
    Assert.Equal(15, status.TotalReasoningTokens);
  }

  [Fact]
  public void ReportedZeroCache_IsMeasuredCold_NotUnreported()
  {
    // cached_tokens: 0 is a real prompt-cache miss and must render as 0%, which is a
    // different fact from the provider saying nothing at all.
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(500, 20, 0, null, 0), new ContextComposition(1, 1, 1));

    ContextStatus status = accountant.Status;
    Assert.Equal(0, status.LastCachedInputTokens);
    Assert.Equal(0.0, status.CacheHitPercent);
    Assert.Equal(0, status.LastReasoningTokens);
  }

  [Fact]
  public void BeforeFirstReport_CacheAndReasoningAreUnreported()
  {
    ContextStatus status = new ContextAccountant(1000).Status;

    Assert.Null(status.LastCachedInputTokens);
    Assert.Null(status.LastReasoningTokens);
    Assert.Null(status.CacheHitPercent);
    Assert.Equal(0, status.TotalCachedInputTokens);
    Assert.Equal(0, status.TotalReasoningTokens);
  }

  [Fact]
  public void ThresholdBoundary_ExactlyEightyPercent_IsReached()
  {
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(800, 10), new ContextComposition(1, 1, 1));

    Assert.Equal(80.0, accountant.Status.UtilizationPercent);
  }
}
