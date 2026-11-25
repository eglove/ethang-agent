using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using OpenCvSharp;

namespace eThangAgent.Vision.ACL.Tests;

/// <summary>Template matching against real OpenCV: a synthetic raster with a known
///     shape at a known place, so the reported bounds are checked against the truth
///     the fixture drew rather than against the implementation's own output.</summary>
public class OpenCvTemplateLocatorTests
{
  /// <summary>A 400x300 black raster carrying a 40x30 textured patch whose top-left
  ///     corner sits at (120, 90). The patch is textured rather than flat so the template
  ///     has the variation normalised correlation needs to be well-conditioned.</summary>
  private static (byte[] Raster, byte[] Needle) RasterWithBlock()
  {
    using Mat raster = new(300, 400, MatType.CV_8UC3, Scalar.All(0));
    using Mat patch = TexturedPatch(40, 30);
    using Mat destination = new(raster, new OpenCvSharp.Rect(120, 90, 40, 30));
    patch.CopyTo(destination);
    using Mat needle = new(raster, new OpenCvSharp.Rect(120, 90, 40, 30));
    _ = Cv2.ImEncode(".png", raster, out byte[] rasterBytes);
    _ = Cv2.ImEncode(".png", needle, out byte[] needleBytes);
    return (rasterBytes, needleBytes);
  }

  /// <summary>A deterministic high-contrast pattern: variation in both axes, so a
  ///     match is unambiguous and the fixture never depends on randomness.</summary>
  private static Mat TexturedPatch(int width, int height)
  {
    Mat patch = new(height, width, MatType.CV_8UC3, Scalar.All(0));
    for (int y = 0; y < height; y++)
    {
      for (int x = 0; x < width; x++)
      {
        byte value = (byte)(((x * 37) + (y * 71)) % 256);
        patch.Set(y, x, new Vec3b(value, (byte)(value / 2), (byte)(255 - value)));
      }
    }

    return patch;
  }

  /// <summary>Alternating high-contrast bands, oriented on one axis.</summary>
  private static Mat Stripes(int width, int height, bool horizontal)
  {
    Mat stripes = new(height, width, MatType.CV_8UC3, Scalar.All(0));
    for (int y = 0; y < height; y++)
    {
      for (int x = 0; x < width; x++)
      {
        int band = horizontal ? y : x;
        byte value = (byte)(band / 4 % 2 == 0 ? 255 : 0);
        stripes.Set(y, x, new Vec3b(value, value, value));
      }
    }

    return stripes;
  }

  [Fact]
  public void Locate_FindsTheBlockAtItsTruePosition()
  {
    (byte[] raster, byte[] needle) = RasterWithBlock();

    Result<IReadOnlyList<VisionMatch>> result = OpenCvTemplateLocator.Locate(raster, needle, 0.9);

    Assert.True(result.IsSuccess);
    VisionMatch match = Assert.Single(result.Value);
    Assert.Equal(120, match.Bounds.X);
    Assert.Equal(90, match.Bounds.Y);
    Assert.Equal(40, match.Bounds.Width);
    Assert.Equal(30, match.Bounds.Height);
    Assert.True(match.Score > 0.99, $"expected a near-exact score, got {match.Score}");
  }

  [Fact]
  public void Locate_ReportsOneMatchPerVisualObject_NotASmearOfNeighbours()
  {
    (byte[] raster, byte[] needle) = RasterWithBlock();

    Result<IReadOnlyList<VisionMatch>> result = OpenCvTemplateLocator.Locate(raster, needle, 0.5);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value);
  }

  [Fact]
  public void Locate_ReturnsEmpty_WhenTheNeedleIsNotPresent()
  {
    // Both rasters carry variation, so the search is well-posed; the needle's shape
    // simply never occurs in the raster. Horizontal stripes against vertical stripes
    // are orthogonal, so no position correlates.
    using Mat raster = Stripes(200, 200, horizontal: true);
    using Mat needle = Stripes(20, 20, horizontal: false);
    _ = Cv2.ImEncode(".png", raster, out byte[] rasterBytes);
    _ = Cv2.ImEncode(".png", needle, out byte[] needleBytes);

    Result<IReadOnlyList<VisionMatch>> result = OpenCvTemplateLocator.Locate(rasterBytes, needleBytes, 0.9);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value);
  }

  [Fact]
  public void Locate_RefusesAUniformNeedle_BecauseItWouldMatchEverywhere()
  {
    // A zero-variance template makes normalised correlation undefined; OpenCV reports
    // 1.0 at every position, so an unguarded search "finds" a blank patch everywhere.
    using Mat raster = new(200, 200, MatType.CV_8UC3, Scalar.All(0));
    Cv2.Rectangle(raster, new OpenCvSharp.Rect(10, 10, 20, 20), Scalar.All(255), -1);
    using Mat needle = new(20, 20, MatType.CV_8UC3, Scalar.All(128));
    _ = Cv2.ImEncode(".png", raster, out byte[] rasterBytes);
    _ = Cv2.ImEncode(".png", needle, out byte[] needleBytes);

    Result<IReadOnlyList<VisionMatch>> result = OpenCvTemplateLocator.Locate(rasterBytes, needleBytes, 0.9);

    Assert.False(result.IsSuccess);
    Assert.Equal("vision_template_uniform", result.Error.Code);
  }

  [Fact]
  public void Locate_RefusesANeedleLargerThanTheRaster()
  {
    using Mat raster = new(50, 50, MatType.CV_8UC3, Scalar.All(0));
    using Mat needle = new(100, 100, MatType.CV_8UC3, Scalar.All(255));
    _ = Cv2.ImEncode(".png", raster, out byte[] rasterBytes);
    _ = Cv2.ImEncode(".png", needle, out byte[] needleBytes);

    Result<IReadOnlyList<VisionMatch>> result = OpenCvTemplateLocator.Locate(rasterBytes, needleBytes, 0.9);

    Assert.False(result.IsSuccess);
    Assert.Equal("vision_template_out_of_range", result.Error.Code);
  }

  [Fact]
  public void Locate_RefusesUndecodableBytes()
  {
    Result<IReadOnlyList<VisionMatch>> result =
        OpenCvTemplateLocator.Locate([1, 2, 3, 4], [5, 6, 7], 0.9);

    Assert.False(result.IsSuccess);
    Assert.Equal("vision_undecodable_raster", result.Error.Code);
  }
}
