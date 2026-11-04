namespace eThangAgent.ToolDomain.Tests;

/// <summary>The read tool's output format, pinned as exact strings: the annotation
///     line, numbered content, clamp warning, and both freshness suffixes.</summary>
public class ReadOutputRendererTests
{
  private static readonly FileVersion V1 = new(100, new DateTime(638000000000000000, DateTimeKind.Utc));

  [Fact]
  public void RendersHeaderAndNumberedLines()
  {
    FileRead file = new(["alpha", "beta"], LastLineRead: 2, TotalLines: 2, Version: null);
    string output = ReadOutputRenderer.Render("C:/w/a.txt", 1, 2, file, new FileCoverage([]), forced: false);
    Assert.Equal("[read C:/w/a.txt lines 1-2 of 2 total]" + Environment.NewLine +
                 "1→ alpha" + Environment.NewLine + "2→ beta", output);
  }

  [Fact]
  public void AppendsClampWarningWhenEndLineExceedsFile()
  {
    FileRead file = new(["only"], LastLineRead: 1, TotalLines: 1, Version: null);
    string output = ReadOutputRenderer.Render("a.txt", 1, 1000, file, new FileCoverage([]), forced: false);
    Assert.Contains("[read a.txt lines 1-1 of 1 total]", output, StringComparison.Ordinal);
    Assert.EndsWith("[warning] endLine 1000 exceeded file length (1); clamped", output, StringComparison.Ordinal);
  }

  [Fact]
  public void FullyCoveredRangeElidesContent()
  {
    FileRead file = new(["alpha", "beta"], LastLineRead: 2, TotalLines: 2, Version: V1);
    FileCoverage coverage = new([new LineSpan(1, 2)]);
    string output = ReadOutputRenderer.Render("a.txt", 1, 2, file, coverage, forced: false);
    Assert.Equal(
        "[read a.txt lines 1-2 of 2 total | " + V1.Token + " | unchanged since your last read; content elided]",
        output);
  }

  [Fact]
  public void ForcedReadAppendsForcedSuffixAndKeepsContent()
  {
    FileRead file = new(["alpha", "beta"], LastLineRead: 2, TotalLines: 2, Version: V1);
    FileCoverage coverage = new([new LineSpan(1, 2)]);
    string output = ReadOutputRenderer.Render("a.txt", 1, 2, file, coverage, forced: true);
    Assert.Equal(
        "[read a.txt lines 1-2 of 2 total | " + V1.Token + " | forced]" + Environment.NewLine +
        "1→ alpha" + Environment.NewLine + "2→ beta", output);
  }

  [Fact]
  public void PartiallyCoveredRangeNamesHeldSpansAndOmitsTheirLines()
  {
    FileRead file = new(["alpha", "beta", "gamma"], LastLineRead: 3, TotalLines: 3, Version: V1);
    FileCoverage coverage = new([new LineSpan(2, 2)]);
    string output = ReadOutputRenderer.Render("a.txt", 1, 3, file, coverage, forced: false);
    Assert.Equal(
        "[read a.txt lines 1-3 of 3 total | " + V1.Token + " | lines 2-2 elided (unchanged since your last read)]" +
        Environment.NewLine + "1→ alpha" + Environment.NewLine + "3→ gamma", output);
  }
}
