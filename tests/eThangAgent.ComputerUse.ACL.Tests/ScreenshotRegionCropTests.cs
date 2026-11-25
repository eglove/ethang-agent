namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>The region crop: a zoomed observation must deliver a raster whose coordinate
///     space still resolves to the right screen point. Two halves are pinned here - the
///     crop's pixel geometry (clamped to the raster, resampled to the delivery ceiling)
///     and the screen sub-rect the cropped frame registers over.</summary>
public class ScreenshotRegionCropTests
{
  [Fact]
  public void ClampToRaster_KeepsAnInBoundsRegionExactly()
  {
    (System.Windows.Int32Rect crop, int w, int h) = ScreenshotEncoder.ClampToRaster(
        new ToolDomain.VisionRect(100, 200, 300, 150), 1280, 698);

    Assert.Equal(100, crop.X);
    Assert.Equal(200, crop.Y);
    Assert.Equal(300, w);
    Assert.Equal(150, h);
  }

  [Fact]
  public void ClampToRaster_TrimsARegionThatRunsOffTheEdge()
  {
    // A region read off an image can overshoot by a rounding pixel; the part that exists
    // is what gets cropped, and the crop never runs past the raster.
    (System.Windows.Int32Rect crop, int w, int h) = ScreenshotEncoder.ClampToRaster(
        new ToolDomain.VisionRect(1200, 650, 400, 400), 1280, 698);

    Assert.Equal(1200, crop.X);
    Assert.Equal(650, crop.Y);
    Assert.Equal(80, w);
    Assert.Equal(48, h);
  }

  [Fact]
  public void ClampToRaster_KeepsAtLeastOnePixel_WhenTheRegionStartsPastTheRaster()
  {
    (System.Windows.Int32Rect crop, int w, int h) = ScreenshotEncoder.ClampToRaster(
        new ToolDomain.VisionRect(5000, 5000, 10, 10), 1280, 698);

    Assert.Equal(1279, crop.X);
    Assert.Equal(697, crop.Y);
    Assert.Equal(1, w);
    Assert.Equal(1, h);
  }

  [Fact]
  public void FitTo_MagnifiesASmallCropUpToTheCeiling()
  {
    // The whole point of the crop: a region whose text was too small to read arrives at
    // the largest raster the delivery bound allows, not at its original size.
    (int w, int h) = ScreenshotEncoder.FitTo(300, 150, ScreenshotEncoder.MaxEdge);

    Assert.Equal(1280, w);
    Assert.Equal(640, h);
  }
}
