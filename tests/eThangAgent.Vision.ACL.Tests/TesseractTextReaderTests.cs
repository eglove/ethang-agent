using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using OpenCvSharp;

namespace eThangAgent.Vision.ACL.Tests;

/// <summary>OCR against real Tesseract: text is rendered to a raster at a known
///     position, so the reported word and its box are checked against what the
///     fixture drew rather than against the engine's own idea of the truth.</summary>
public class TesseractTextReaderTests
{
  /// <summary>A white 600x200 raster with black text starting at (40, 60).</summary>
  private static byte[] RasterWithText(string text, out int originX, out int originY)
  {
    using Mat raster = new(200, 600, MatType.CV_8UC3, Scalar.All(255));
    const int origin = 40;
    originX = origin;
    originY = 60;
    Cv2.PutText(raster, text, new Point(origin, 100), HersheyFonts.HersheyDuplex, 2.0,
        Scalar.All(0), 3, LineTypes.AntiAlias);
    _ = Cv2.ImEncode(".png", raster, out byte[] bytes);
    return bytes;
  }

  [Fact]
  public void ReadText_ReadsTheWordAndReportsItsBox()
  {
    byte[] raster = RasterWithText("HELLO", out int originX, out int originY);

    Result<IReadOnlyList<VisionWord>> result = TesseractTextReader.ReadText(raster, null);

    Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : result.Error.Message);
    VisionWord word = Assert.Single(result.Value);
    Assert.Equal("HELLO", word.Text);
    // The box must sit where the fixture drew the text, within OCR's own tolerance.
    Assert.InRange(word.Bounds.X, originX - 15, originX + 15);
    Assert.InRange(word.Bounds.Y, originY - 25, originY + 25);
    Assert.True(word.Confidence > 0.5, $"expected usable confidence, got {word.Confidence}");
  }

  [Fact]
  public void ReadText_ReportsBoundsInFullRasterSpace_WhenGivenARegion()
  {
    byte[] raster = RasterWithText("HELLO", out int originX, out _);

    // A region covering the text: the word's box must still be reported in the
    // FULL raster's coordinates, so a region read and a whole read agree.
    Result<IReadOnlyList<VisionWord>> result =
        TesseractTextReader.ReadText(raster, new VisionRect(0, 0, 400, 200));

    Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : result.Error.Message);
    VisionWord word = Assert.Single(result.Value);
    Assert.Equal("HELLO", word.Text);
    Assert.InRange(word.Bounds.X, originX - 15, originX + 15);
  }

  [Fact]
  public void ReadText_RefusesARegionOutsideTheRaster()
  {
    byte[] raster = RasterWithText("HELLO", out _, out _);

    Result<IReadOnlyList<VisionWord>> result =
        TesseractTextReader.ReadText(raster, new VisionRect(500, 150, 400, 400));

    Assert.False(result.IsSuccess);
    Assert.Equal("vision_region_out_of_range", result.Error.Code);
  }

  [Fact]
  public void ReadText_RefusesUndecodableBytes()
  {
    Result<IReadOnlyList<VisionWord>> result = TesseractTextReader.ReadText([9, 8, 7], null);

    Assert.False(result.IsSuccess);
    Assert.Equal("vision_undecodable_raster", result.Error.Code);
  }
}
