namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 4 (finding 2 pins): the coordinate mapping MUST read the extents
///     SM_CXVIRTUALSCREEN=78 / SM_CYVIRTUALSCREEN=79 - never the origins 76/77, whose
///     zero value produced the committed divide-by-zero. The metric indexes are pinned
///     as constants, and the real machine's extents are asserted positive so the
///     production mapping has sane inputs to fetch.</summary>
public partial class MetricsPinTests
{
  [Fact]
  public void ExtentMetricIndexes_Are78And79_TheVirtualScreenExtents()
  {
    Assert.Equal(78, NativeInput.ExtentWidthMetric);
    Assert.Equal(79, NativeInput.ExtentHeightMetric);
  }

  [Fact]
  public void ExtentMetricIndexes_AreNotTheOriginMetrics()
  {
    Assert.NotEqual(76, NativeInput.ExtentWidthMetric);
    Assert.NotEqual(77, NativeInput.ExtentHeightMetric);
  }

  [Fact]
  public void GetSystemMetrics_Extents_ArePositiveOnThisDesktop_ProductionInputsSane()
  {
    // Production fetches these exact indexes; a degenerate machine would see the
    // honest-refusal path (BuildDragRows yields no rows), never a division by zero.
    Assert.True(GetSystemMetrics(78) > 0, "SM_CXVIRTUALSCREEN must be positive here");
    Assert.True(GetSystemMetrics(79) > 0, "SM_CYVIRTUALSCREEN must be positive here");
  }

  [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
  [System.Runtime.InteropServices.LibraryImport("user32.dll")]
  private static partial int GetSystemMetrics(int index);
}
