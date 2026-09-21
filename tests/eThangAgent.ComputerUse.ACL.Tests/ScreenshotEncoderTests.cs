using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Screenshot-encode ladder contract (the batch brief, task 15): the
///     1280 longest-edge downscale, the quality ladder 75/60/45 over the floor
///     40, the 0.8 shrink steps over the 320 floor, the 190 KiB base64 bound,
///     and the blank passthrough. Uses synthetic solid-color PNG fixtures built
///     with WPF imaging itself.
///     </summary>
public class ScreenshotEncoderTests
{
  [Fact]
  public void Encode_SmallSolidPng_KeepsSizeAndQuality75()
  {
    byte[] png = SolidPng(300, 200, Colors.CornflowerBlue);

    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);

    Assert.Equal(300, delivered.Width);
    Assert.Equal(200, delivered.Height);
    Assert.Equal(ScreenshotEncoder.StartQuality, delivered.Quality);
    Assert.True(delivered.Base64.Length <= ScreenshotEncoder.MaxBase64Length);
  }

  [Fact]
  public void Encode_LargePng_DownscalesLongestEdgeToTheCeiling()
  {
    byte[] png = SolidPng(2560, 1440, Colors.DarkGreen);

    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);

    Assert.Equal(1280, delivered.Width);
    Assert.Equal(720, delivered.Height);
    Assert.True(delivered.Base64.Length <= ScreenshotEncoder.MaxBase64Length);
  }

  [Fact]
  public void Encode_NoisyOversizedPng_WalksQualityLadderDownFrom75()
  {
    // A 2px black/white checkerboard is the DCT worst case: it resists JPEG
    // compression, so the 1280x1280 raster's encoded size at quality 75 lands
    // over the base64 bound and the ladder must step DOWN from 75.
    byte[] png = NoisePng(2400, 2400);

    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);

    Assert.True(delivered.Quality < ScreenshotEncoder.StartQuality,
        $"expected a stepped-down quality, got {delivered.Quality}");
    Assert.True(delivered.Base64.Length <= ScreenshotEncoder.MaxBase64Length);
  }

  [Fact]
  public void Encode_QualityLadderFloorsAt40_ThenShrinks()
  {
    // Big worst-case raster: quality ladder exhausts, ladder shrinks 0.8x until
    // the bound fits; final edge stays at or over the 320 floor (or the floor
    // was reached and delivery is over-bound by design).
    byte[] png = NoisePng(2400, 2400);

    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);

    Assert.True(delivered.Quality >= ScreenshotEncoder.QualityFloor,
        $"quality {delivered.Quality} sank under the floor 40");
    bool fits = delivered.Base64.Length <= ScreenshotEncoder.MaxBase64Length;
    bool floored = Math.Min(delivered.Width, delivered.Height) <= 320;
    Assert.True(fits || floored, "neither the bound was met nor the shrink floor reached");
  }

  [Fact]
  public void Encode_CompositesAlphaOntoWhite()
  {
    // A fully transparent pixel must read as white after the composite, not black.
    byte[] png = TransparentPng(64, 64);

    DeliveredScreenshot delivered = ScreenshotEncoder.Encode(png);
    byte[] rgb = DecodeJpegPixels(delivered.Jpeg, out int w, out int h);

    Assert.Equal(64, w);
    Assert.Equal(64, h);
    // JPEG is lossy; assert the mean channel is near-white (>= 240).
    double mean = 0;
    for (int i = 0; i < rgb.Length; i++)
    {
      mean += rgb[i];
    }

    mean /= rgb.Length;
    Assert.True(mean >= 240, $"transparent raster did not composite onto white; mean {mean:0.0}");
  }

  [Fact]
  public void Blank_PassthroughCarriesNoBytesAndTheReason()
  {
    DeliveredScreenshot blank = ScreenshotEncoder.Blank(640, 480, "blank raster withheld");

    Assert.Equal(640, blank.Width);
    Assert.Equal(480, blank.Height);
    Assert.Empty(blank.Jpeg);
    Assert.Equal("blank raster withheld", blank.Base64);
    Assert.Equal(ScreenshotEncoder.BlankQuality, blank.Quality);
  }

  // ---- fixtures ----

  private static byte[] SolidPng(int w, int h, Color color)
  {
    int stride = ((w * 4) + 3) & ~3;
    byte[] pixels = new byte[stride * h];
    for (int row = 0; row < h; row++)
    {
      for (int col = 0; col < w; col++)
      {
        pixels[(row * stride) + (col * 4)] = color.B;
        pixels[(row * stride) + (col * 4) + 1] = color.G;
        pixels[(row * stride) + (col * 4) + 2] = color.R;
        pixels[(row * stride) + (col * 4) + 3] = 0xFF;
      }
    }

    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(
        BitmapSource.Create(w, h, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride)));
    using MemoryStream ms = new();
    encoder.Save(ms);
    return ms.ToArray();
  }

  private static byte[] NoisePng(int w, int h)
  {
#pragma warning disable CA5394 // Named decision: the seed makes the fixture DETERMINISTIC; no security property is involved.
    Random random = new(1234);
    int stride = ((w * 4) + 3) & ~3;
    byte[] pixels = new byte[stride * h];
    for (int i = 0; i < w * h; i++)
    {
      pixels[i * 4] = (byte)random.Next(256);
      pixels[(i * 4) + 1] = (byte)random.Next(256);
      pixels[(i * 4) + 2] = (byte)random.Next(256);
      pixels[(i * 4) + 3] = 0xFF;
    }
#pragma warning restore CA5394

    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(
        BitmapSource.Create(w, h, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride)));
    using MemoryStream ms = new();
    encoder.Save(ms);
    return ms.ToArray();
  }

  private static byte[] TransparentPng(int w, int h)
  {
    int stride = ((w * 4) + 3) & ~3;
    byte[] pixels = new byte[stride * h]; // all zero = fully transparent
    PngBitmapEncoder encoder = new();
    encoder.Frames.Add(BitmapFrame.Create(
        BitmapSource.Create(w, h, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride)));
    using MemoryStream ms = new();
    encoder.Save(ms);
    return ms.ToArray();
  }

  private static byte[] DecodeJpegPixels(byte[] jpeg, out int width, out int height)
  {
    JpegBitmapDecoder decoder = new(
        new MemoryStream(jpeg), BitmapCreateOptions.None,
        BitmapCacheOption.OnLoad);
    BitmapFrame frame = decoder.Frames[0];
    width = frame.PixelWidth;
    height = frame.PixelHeight;
    int stride = ((width * frame.Format.BitsPerPixel / 8) + 3) & ~3;
    byte[] pixels = new byte[stride * height];
    frame.CopyPixels(pixels, stride, 0);
    return pixels;
  }
}
