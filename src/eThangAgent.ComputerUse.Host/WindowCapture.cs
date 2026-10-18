using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The window-scoped capturer (task 17, fix round I3): the blank-detection math
///     and the minimize policy are unit-tested; the REAL GDI capture path
///     (PrintWindow/PW_RENDERFULLCONTENT/BitBlt, pointer rect, DPI note) needs a live
///     desktop and is deferred to Task 18 integration - no unit coverage is claimed for it.</summary>
public static partial class WindowCapture
{

  /// <summary>The minimize policy: true when the caller wants pixels. Observation
  ///     (tree-only) passes false and a minimized window is captured as-is (blank).</summary>
  public static bool ShouldRestoreForCapture(bool includeScreenshot) => includeScreenshot;

  /// <summary>Blank detection over BGRA pixels: all-alpha-zero (fully transparent) or
  ///     near-uniform (every sampled pixel identical within tolerance) frames carry no
  ///     actionable content. Sampling strides keep this O(n/64) for big windows.</summary>
  public static bool IsBlank(ReadOnlySpan<byte> bgra, int stride, int height, int tolerance = 2)
  {
    if (bgra.IsEmpty || stride < 4 || height <= 0)
    {
      return true;
    }

    BlankScan scan = new(tolerance);
    int sampleStep = Math.Max(1, bgra.Length / 4 / 4096);
    for (int y = 0; y < height; y++)
    {
      int row = y * stride;
      for (int x = 0; x + 3 < stride; x += 4 * sampleStep)
      {
        int o = row + x;
        if (!scan.Sample(bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]))
        {
          return false; // two clearly different opaque-ish pixels: not blank
        }
      }
    }

    return scan.IsBlankSoFar; // fully transparent, or fewer than two distinct samples
  }

  /// <summary>Accumulates the blank-detection scan state across sampled pixels: the
  ///     any-alpha flag, the first sample's color, and the sample count. Sample
  ///     returns false the moment two clearly different opaque-ish pixels prove the
  ///     frame is not blank.</summary>
  private struct BlankScan(int tolerance)
  {
    private readonly int Tolerance { get; } = tolerance;
    private bool AnyAlpha { get; set; }
    private byte FirstB { get; set; }
    private byte FirstG { get; set; }
    private byte FirstR { get; set; }
    private bool FirstTaken { get; set; }
    private int Sampled { get; set; }

    public bool Sample(byte b, byte g, byte r, byte a)
    {
      if (a != 0)
      {
        AnyAlpha = true;
      }

      if (!FirstTaken)
      {
        FirstB = b;
        FirstG = g;
        FirstR = r;
        FirstTaken = true;
      }
      else if (Math.Abs(b - FirstB) > Tolerance || Math.Abs(g - FirstG) > Tolerance || Math.Abs(r - FirstR) > Tolerance)
      {
        return false;
      }

      Sampled++;
      return true;
    }

    /// <summary>Fully transparent, or fewer than two distinct samples.</summary>
    public readonly bool IsBlankSoFar => !AnyAlpha || Sampled <= 1;
  }
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [LibraryImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);
};
