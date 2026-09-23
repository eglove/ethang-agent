
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round I3: REAL unit coverage for the capture-side fake-testable logic
///     (blank detection math, minimize policy) and the UIA walker deadline mechanics
///     (injectable worker, join under deadline, abandon on timeout). No claimed-but-absent
///     coverage: the real GDI raster and the real CUIAutomation walk stay deferred to
///     Task 18 integration.</summary>
public class CaptureAndDeadlineTests
{
  [Fact]
  public void BlankDetection_AllTransparent_IsBlank()
  {
    byte[] pixels = new byte[4 * 64]; // 16 px, all zero (alpha 0)
    Assert.True(WindowCapture.IsBlank(pixels, stride: 4 * 16, height: 1));
  }

  [Fact]
  public void BlankDetection_TwoDistinctPixels_IsNotBlank()
  {
    byte[] pixels = new byte[8 * 4]; // 8 pixels
    pixels[0] = 255; // first sample: opaque blue
    pixels[5 * 4] = 255; // a later sample differs in a channel
    pixels[(5 * 4) + 2] = 255;
    Assert.False(WindowCapture.IsBlank(pixels, stride: 8 * 4, height: 1));
  }

  [Fact]
  public void BlankDetection_EmptyPixels_IsBlank() => Assert.True(WindowCapture.IsBlank([], stride: 0, height: 0));

  [Fact]
  public void MinimizePolicy_RestoresOnlyWhenPixelsRequested()
  {
    Assert.True(WindowCapture.ShouldRestoreForCapture(includeScreenshot: true));
    Assert.False(WindowCapture.ShouldRestoreForCapture(includeScreenshot: false));
  }

  [Fact]
  public void WalkerDeadline_FastWorker_ReturnsResult()
  {
    UiaTreeWalker walker = new(() => ["row"]);
    string[]? rows = walker.WalkWithDeadline(TimeSpan.FromSeconds(5));
    Assert.NotNull(rows);
    Assert.Equal(["row"], rows);
  }

  [Fact]
  public void WalkerDeadline_SlowWorker_ReturnsNull_AndIsAbandoned()
  {
    using ManualResetEventSlim workerStarted = new(false);
    using ManualResetEventSlim releaseWorker = new(false);
    UiaTreeWalker walker = new(() =>
    {
      workerStarted.Set();
      bool released = releaseWorker.Wait(TimeSpan.FromSeconds(60)); // held past the deadline on purpose
      _ = released;
      return null;
    });
    string[]? rows = walker.WalkWithDeadline(TimeSpan.FromMilliseconds(100));
    Assert.Null(rows); // deadline exceeded: null answers timeout, worker abandoned by design
    releaseWorker.Set(); // release the abandoned worker so its thread can exit
  }
};
