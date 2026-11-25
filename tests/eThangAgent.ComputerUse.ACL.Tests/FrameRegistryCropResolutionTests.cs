namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>A cropped observation registers its frame over the SCREEN SUB-RECT the crop
///     covers, so a coordinate the model reads off the zoomed image resolves to the point
///     it meant. These pin the round trip end to end: crop fraction -> window sub-rect ->
///     screen point, and back through FrameRegistry's pixel-center mapping.</summary>
public class FrameRegistryCropResolutionTests
{
  [Fact]
  public void CroppedFrame_ResolvesTheCentreOfTheCrop_ToTheCentreOfItsScreenSubRect()
  {
    // A 1936x1056 window; the model asked for the region 400,200 800x600 of that raster.
    // The crop's centre (400,300 within the crop) must land at the centre of the screen
    // rect that region covers: x 400+400=800 of 1936, y 200+300=500 of 1056.
    FrameRegistry registry = new();
    ScreenshotWindowRect subRect = SubRectFor(region: (400, 200, 800, 600), raster: (1936, 1056), window: (0, 0, 1936, 1056));
    _ = registry.Add(1280, 960, subRect, actionable: true);

    FrameCoordinateResult result = registry.ResolveForCoordinate("latest", 640, 480);

    FrameCoordinateResult.Hit hit = Assert.IsType<FrameCoordinateResult.Hit>(result);
    Assert.Equal(800, hit.Resolution.X);
    Assert.Equal(500, hit.Resolution.Y);
  }

  [Fact]
  public void CroppedFrame_MapsTheCropOrigin_ToTheRegionOrigin()
  {
    FrameRegistry registry = new();
    ScreenshotWindowRect subRect = SubRectFor(region: (400, 200, 800, 600), raster: (1936, 1056), window: (0, 0, 1936, 1056));
    _ = registry.Add(1280, 960, subRect, actionable: true);

    FrameCoordinateResult result = registry.ResolveForCoordinate("latest", 0, 0);

    FrameCoordinateResult.Hit hit = Assert.IsType<FrameCoordinateResult.Hit>(result);
    Assert.Equal(400, hit.Resolution.X);
    Assert.Equal(200, hit.Resolution.Y);
  }

  [Fact]
  public void CroppedFrame_RespectsANonZeroWindowOrigin()
  {
    // The window sits at screen 100,50; the sub-rect must carry that offset through.
    FrameRegistry registry = new();
    ScreenshotWindowRect subRect = SubRectFor(region: (0, 0, 968, 528), raster: (1936, 1056), window: (100, 50, 1936, 1056));
    _ = registry.Add(1280, 698, subRect, actionable: true);

    FrameCoordinateResult result = registry.ResolveForCoordinate("latest", 0, 0);

    FrameCoordinateResult.Hit hit = Assert.IsType<FrameCoordinateResult.Hit>(result);
    Assert.Equal(100, hit.Resolution.X);
    Assert.Equal(50, hit.Resolution.Y);
  }

  // The ACL's own mapping, restated here so the test pins the contract rather than
  // re-deriving it from the production code it is meant to check.
  private static ScreenshotWindowRect SubRectFor((int X, int Y, int W, int H) region,
      (int W, int H) raster, (int X, int Y, int W, int H) window)
  {
    double fx = region.X / (double)raster.W;
    double fy = region.Y / (double)raster.H;
    double fw = region.W / (double)raster.W;
    double fh = region.H / (double)raster.H;
    return new ScreenshotWindowRect(
        window.X + (int)Math.Floor(fx * window.W),
        window.Y + (int)Math.Floor(fy * window.H),
        Math.Max(1, (int)Math.Floor(fw * window.W)),
        Math.Max(1, (int)Math.Floor(fh * window.H)));
  }
}
