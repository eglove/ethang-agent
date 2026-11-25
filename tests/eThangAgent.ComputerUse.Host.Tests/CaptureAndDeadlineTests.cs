
namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Real unit coverage for the capture-side fake-testable logic (blank detection
///     math, minimize policy) and the UIA call bound the walk depends on. No
///     claimed-but-absent coverage: the real GDI raster and the live walk stay
///     integration concerns.</summary>
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
  public void Session_BoundsEveryUiaCall()
  {
    // The walk's timeout answer is the automation's own bound, not a harness deadline:
    // a provider that stops answering fails its call instead of hanging the request.
    Assert.True(Uia3Session.ConnectionTimeout > TimeSpan.Zero);
    Assert.True(Uia3Session.TransactionTimeout > Uia3Session.ConnectionTimeout);
  }
};
