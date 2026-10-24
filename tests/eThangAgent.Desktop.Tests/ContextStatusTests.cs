using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.Tests;

public class ContextStatusTests
{
  [Fact]
  public void SetContext_WithWindow_FormatsCtxLine()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(
        new ContextStatus(148_200, 148_200, 9_000, 1_000_000, 14.82),
        new ContextBreakdown(12_000, 120_100, 16_000)));

    Assert.Equal("CTX 148.2K/1M, 15%", s.ContextDisplay);
    Assert.Contains("System ~12K", s.ContextBreakdownText, StringComparison.Ordinal);
    Assert.Contains("Messages ~120.1K", s.ContextBreakdownText, StringComparison.Ordinal);
    Assert.Contains("Tools ~16K", s.ContextBreakdownText, StringComparison.Ordinal);
  }

  [Fact]
  public void SetContext_UnknownWindow_ShowsTotalsOnly()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(new ContextStatus(148_200, 148_200, 0, null, null), null));

    Assert.Equal("CTX 148.2K total", s.ContextDisplay);
    Assert.Equal("", s.ContextBreakdownText);
  }

  [Fact]
  public void SetContext_NullClearsDisplay()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(new ContextStatus(500, 500, 0, 1000, 50), null));
    Assert.NotEqual("", s.ContextDisplay);

    s.SetContext(null);

    Assert.Equal("", s.ContextDisplay);
    Assert.Equal("", s.ContextBreakdownText);
  }

  [Fact]
  public void SetContext_WithReportedCacheAndReasoning_AppendsBothSegments()
  {
    // issue #262: the two provider-scored numbers the note calls decisive appear on
    // the status line, in a fixed order after the utilization.
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(
        new ContextStatus(148_200, 148_200, 9_000, 1_000_000, 14.82,
            LastCachedInputTokens: 120_000, TotalCachedInputTokens: 120_000,
            LastReasoningTokens: 1_200, TotalReasoningTokens: 1_200),
        null));

    Assert.Equal("CTX 148.2K/1M, 15%, cache 81%, reasoning 1.2K", s.ContextDisplay);
  }

  [Fact]
  public void SetContext_NoReportedCacheOrReasoning_RendersExactlyAsBefore()
  {
    // The pre-#262 rendering is byte-identical when the provider reports neither
    // number: no dangling separator, no empty label.
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(
        new ContextStatus(148_200, 148_200, 9_000, 1_000_000, 14.82), null));

    Assert.Equal("CTX 148.2K/1M, 15%", s.ContextDisplay);
  }

  [Fact]
  public void SetContext_ReportedZeroCache_RendersZeroPercent()
  {
    // A reported cold cache is a measurement, not silence: 0% renders.
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(
        new ContextStatus(1000, 1000, 10, 10_000, 10.0,
            LastCachedInputTokens: 0, TotalCachedInputTokens: 0,
            LastReasoningTokens: 0, TotalReasoningTokens: 0),
        null));

    Assert.Equal("CTX 1K/10K, 10%, cache 0%, reasoning 0", s.ContextDisplay);
  }

  [Fact]
  public void SetContext_UnknownWindow_StillCarriesTheCacheSegment()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");
    s.SetContext(new ContextSnapshot(
        new ContextStatus(4_000, 4_000, 10, null, null,
            LastCachedInputTokens: 3_000, TotalCachedInputTokens: 3_000),
        null));

    Assert.Equal("CTX 4K total, cache 75%", s.ContextDisplay);
  }

  [Fact]
  public void BeforeFirstUpdate_DisplaysEmpty()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");

    Assert.Equal("", s.ContextDisplay);
    Assert.Equal("", s.ContextBreakdownText);
  }
}
