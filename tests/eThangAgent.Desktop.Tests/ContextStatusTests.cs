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
  public void BeforeFirstUpdate_DisplaysEmpty()
  {
    StatusViewModel s = new("OpenRouter", "m", "Model default");

    Assert.Equal("", s.ContextDisplay);
    Assert.Equal("", s.ContextBreakdownText);
  }
}
