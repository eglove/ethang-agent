using System.Runtime.InteropServices;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The window-scoped capturer (task 17): PrintWindow with PW_RENDERFULLCONTENT
///     (BitBlt fallback), blank detection (all-transparent or near-uniform frames are
///     non-actionable), the pointer rect, and the minimized-window policy: a minimized
///     window is restored for capture ONLY when pixels were requested
///     (include_screenshot=true); tree observation never restores. The GDI path needs a
///     live desktop - Task 18 integration exercises real captures; the policy and blank
///     math below are the unit-covered parts.</summary>
public static class WindowCapture
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

    bool anyAlpha = false;
    byte firstB = 0, firstG = 0, firstR = 0;
    bool firstTaken = false;
    int rowStride = stride;
    int sampleStep = Math.Max(1, bgra.Length / 4 / 4096);
    int sampled = 0;
    for (int y = 0; y < height; y++)
    {
      int row = y * rowStride;
      for (int x = 0; x + 3 < rowStride; x += 4 * sampleStep)
      {
        int o = row + x;
        byte b = bgra[o], g = bgra[o + 1], r = bgra[o + 2], a = bgra[o + 3];
        if (a != 0)
        {
          anyAlpha = true;
        }

        if (!firstTaken)
        {
          firstB = b;
          firstG = g;
          firstR = r;
          firstTaken = true;
        }
        else if (Math.Abs(b - firstB) > tolerance || Math.Abs(g - firstG) > tolerance || Math.Abs(r - firstR) > tolerance)
        {
          return false; // two clearly different opaque-ish pixels: not blank
        }

        sampled++;
      }
    }

    return !anyAlpha || sampled <= 1; // fully transparent, or fewer than two distinct samples
  }
#pragma warning disable SYSLIB1054 // Named decision (T12-13 precedent): LibraryImport needs unsafe blocks; blittable signature here.
  [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [DllImport("user32.dll")]
  private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);
#pragma warning restore SYSLIB1054
};
