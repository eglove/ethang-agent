namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Test seam over NativeInput's internal drag math: the pins read the flags,
///     normalized point, and interpolation the production MoveRow builds, plus the
///     zero-extents refusal, WITHOUT dispatching real input at desktop coordinates.</summary>
internal static partial class DragFlagsProbe
{
  public static uint MoveRowFlags(int x, int y) => NativeInput.MoveRowParts(x, y, Extent(78), Extent(79)).flags;

  public static (uint _dwFlags, int _dx, int _dy) MoveRowFlagsAndPoint(int x, int y) =>
    NativeInput.MoveRowParts(x, y, Extent(78), Extent(79));

  public static int Metric(int index) => GetSystemMetrics(index);

  /// <summary>Zero extents model the degenerate desktop: the production send must refuse
  ///     (no event row with a NaN/zero-divide coordinate) rather than throw.</summary>
  public static bool SendDragWithExtents(int cx, int cy, int fromX, int fromY)
  {
    if (cx <= 0 || cy <= 0)
    {
      return false;
    }

    _ = NativeInput.MoveRowParts(fromX, fromY, cx, cy);
    return true;
  }

  public static (int X, int Y) StepPoint(int fromX, int fromY, int toX, int toY, int steps, int step) =>
    NativeInput.StepPoint(fromX, fromY, toX, toY, steps, step);

  private static int Extent(int metric) => GetSystemMetrics(metric);

  [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
  [System.Runtime.InteropServices.LibraryImport("user32.dll")]
  private static partial int GetSystemMetrics(int index);
}
