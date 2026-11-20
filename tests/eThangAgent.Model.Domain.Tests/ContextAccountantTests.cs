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
  public void CacheHitPercent_CoversTheWholeSession_NotTheLastRequest()
  {
    // The reported rate is the session's: 400 of the 1,100 prompt tokens billed so far
    // came from cache. The LAST request's own rate is 0%, so a rate taken from it would
    // read 0 here.
    ContextAccountant accountant = new(1000);
    ContextComposition composition = new(1, 1, 1);

    accountant.OnRequestUsage(new TokenUsage(500, 20, 400), composition);
    accountant.OnRequestUsage(new TokenUsage(600, 30, 0), composition);

    ContextStatus status = accountant.Status;
    Assert.Equal(400, status.TotalCachedInputTokens);
    Assert.Equal(36.36, status.CacheHitPercent);
  }

  [Fact]
  public void UnreportedCache_StaysNull_AndAddsNothingToTheTotal()
  {
    // A provider that reports no cache count on a call must not be recorded as
    // measured-cold: the session total keeps only what was reported, and a session that
    // never reported one stays null rather than claiming a 0% hit rate.
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(500, 20, 400), new ContextComposition(1, 1, 1));
    accountant.OnRequestUsage(new TokenUsage(600, 30), new ContextComposition(1, 1, 1));

    ContextStatus status = accountant.Status;
    Assert.Equal(400, status.TotalCachedInputTokens);
    Assert.Equal(36.36, status.CacheHitPercent);

    ContextAccountant silent = new(1000);
    silent.OnRequestUsage(new TokenUsage(500, 20), new ContextComposition(1, 1, 1));

    Assert.Null(silent.Status.TotalCachedInputTokens);
    Assert.Null(silent.Status.CacheHitPercent);
  }

  [Fact]
  public void ReportedZeroCache_IsMeasuredCold_NotUnreported()
  {
    // cached_tokens: 0 is a real prompt-cache miss and must render as 0%, which is a
    // different fact from the provider saying nothing at all.
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(500, 20, 0), new ContextComposition(1, 1, 1));

    ContextStatus status = accountant.Status;
    Assert.Equal(0, status.TotalCachedInputTokens);
    Assert.Equal(0.0, status.CacheHitPercent);
  }

  [Fact]
  public void BeforeFirstReport_CacheIsUnreported()
  {
    ContextStatus status = new ContextAccountant(1000).Status;

    Assert.Null(status.TotalCachedInputTokens);
    Assert.Null(status.CacheHitPercent);
  }

  [Fact]
  public void ThresholdBoundary_ExactlyEightyPercent_IsReached()
  {
    ContextAccountant accountant = new(1000);
    accountant.OnRequestUsage(new TokenUsage(800, 10), new ContextComposition(1, 1, 1));

    Assert.Equal(80.0, accountant.Status.UtilizationPercent);
  }
}
