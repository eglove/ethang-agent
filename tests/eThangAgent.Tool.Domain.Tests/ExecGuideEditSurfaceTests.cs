namespace eThangAgent.ToolDomain.Tests;

/// <summary>Issue #252's surface decision, pinned: the exec guide names `edit` as THE
///     patch surface and documents the multi-hunk form, so the model is never pointed at
///     a whole-file rewrite for a multi-place edit. A guide that stops saying this is a
///     regression, not a wording preference.</summary>
public class ExecGuideEditSurfaceTests
{
  [Fact]
  public void Guide_DocumentsHunksModeAsThePatchSurface()
  {
    Assert.Contains("### Editing files", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("`edit` is the patch surface", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("hunks", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("ATOMICALLY", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_StatesAtomsDisjointnessAndTheRetryCheapMiss()
  {
    Assert.Contains("NOTHING is applied", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("HunksOverlap", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("ready-to-use 'old'", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_ForbidsWholeFileRewritesForMultiPlaceEdits()
  {
    Assert.Contains("Never rewrite a whole file to change several lines", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("File.ReadAllLines", ExecGuide.Text, StringComparison.Ordinal);
  }
}
