using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>A re-encoded screenshot the model receives: the delivered JPEG bytes,
///     their size in pixels, and the base64 text. Width/Height are the DELIVERED
///     raster's - the model quotes pixel coordinates against them.</summary>
/// <summary>OverBound is true when both ladder floors were exhausted and the
///     delivered bytes still exceed MaxBase64Length: the raster is delivered
///     over-bound rather than dropped (delivery-state honesty) and the flag
///     lets the caller disclose that. OverBound is false for the Blank
///     passthrough (no encode happened, nothing was delivered over bound).</summary>
#pragma warning disable CA1819 // Named decision: Jpeg is the delivered raster bytes, one buffer handed over, never mutated after; a copied IReadOnlyList would double the allocation per screenshot.
public sealed record DeliveredScreenshot(int Width, int Height, byte[] Jpeg, string Base64, int Quality, bool OverBound = false);
#pragma warning restore CA1819

/// <summary>The screenshot-encode ladder (spec 3.5): decode the broker's PNG
///     (WPF imaging, already on net10.0-windows), alpha-composite onto white,
///     downscale so the longest edge stays at or under 1280 (high-quality), then
///     walk a quality ladder (75, -15 steps, floor 40) and 0.8 shrink steps
///     (floor edge 320) until the base64 fits the delivery bound (190 KiB).
///     Blank detection is the caller's decision: when the broker reports blank,
///     the caller may withhold the raster and pass it through un-encoded.
///     </summary>
public static class ScreenshotEncoder
{
  /// <summary>The longest-edge downscale ceiling.</summary>
  public const int MaxEdge = 1280;

  /// <summary>The starting JPEG quality and its ladder.</summary>
  public const int StartQuality = 75;
  public const int QualityStep = 15;
  public const int QualityFloor = 40;

  /// <summary>Each shrink multiplies both edges by this; the ladder stops before
  ///     an edge would drop below the floor.</summary>
  public const double ShrinkFactor = 0.8;
  public const int MinEdge = 320;

  /// <summary>The delivery bound: the base64 text must not exceed 190 KiB.</summary>
  public const int MaxBase64Length = 194560;

  /// <summary>The base64 length of one JPEG: base64 length is 4/3 of the byte
  ///     count rounded up to a multiple of 4, so the byte bound is 3/4 of it.
  ///     </summary>
  internal static long Base64Length(byte[] bytes) => (bytes.Length + 2) / 3 * 4;

  /// <summary>The Quality marker of a blank passthrough (no encode happened).</summary>
  public const int BlankQuality = 0;

  /// <summary>The blank-raster passthrough the caller returns when the broker
  ///     withheld a screenshot: no bytes and no re-encode; Jpeg is EMPTY and the
  ///     withhold reason travels in the Base64 field (Quality is BlankQuality).
  ///     </summary>
  public static DeliveredScreenshot Blank(int width, int height, string reason)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return new DeliveredScreenshot(width, height, [], reason, BlankQuality);
  }

  /// <summary>Encodes one broker PNG into a delivery-sized JPEG. Never returns
  ///     null: a broker raster always encodes (the ladder's floors guarantee
  ///     termination); a genuinely broken raster is an infrastructure error that
  ///     surfaces as an exception the session access maps to a typed failure.
  ///     </summary>
  public static DeliveredScreenshot Encode(byte[] pngBytes)
  {
    ArgumentNullException.ThrowIfNull(pngBytes);
    if (pngBytes.Length == 0)
    {
      throw new ArgumentException("PNG bytes must not be empty.", nameof(pngBytes));
    }

    BitmapFrame source = Decode(pngBytes);
    FormatConvertedBitmap converted = new(source, PixelFormats.Pbgra32, null, 0);
    int w = converted.PixelWidth;
    int h = converted.PixelHeight;

    // Downscale first to the <= 1280 ceiling (high-quality WIC Fant resampling),
    // then composite onto white in the render pass so translucent pixels have no
    // alpha ambiguity at JPEG time.
    (int targetW, int targetH) = ScaledTo(w, h, MaxEdge);
    TransformedBitmap downscaled = Scaled(converted, w, h, targetW, targetH);
    byte[] pixels = RasterPixels(downscaled, targetW, targetH);

    byte[] jpeg = EncodeJpeg(pixels, targetW, targetH, StartQuality);
    int quality = StartQuality;
    while (Base64Length(jpeg) > MaxBase64Length)
    {
      int next = quality - QualityStep;
      if (next >= QualityFloor)
      {
        quality = next;
        jpeg = EncodeJpeg(pixels, targetW, targetH, quality);
        continue;
      }

      // Quality exhausted: shrink 0.8x and restart the ladder at 75 - unless the
      // shrink floor is reached, where the current raster is delivered at floor
      // quality with OverBound set (a rare oversized raster is delivered
      // over-bound rather than dropped - delivery-state honesty).
      int shrunkW = (int)Math.Floor(targetW * ShrinkFactor);
      int shrunkH = (int)Math.Floor(targetH * ShrinkFactor);
      if (shrunkW < MinEdge || shrunkH < MinEdge)
      {
        break;
      }

      targetW = shrunkW;
      targetH = shrunkH;
      downscaled = Scaled(converted, w, h, targetW, targetH);
      pixels = RasterPixels(downscaled, targetW, targetH);
      quality = StartQuality;
      jpeg = EncodeJpeg(pixels, targetW, targetH, quality);
    }

    bool overBound = Base64Length(jpeg) > MaxBase64Length;
    return new DeliveredScreenshot(targetW, targetH, jpeg, Convert.ToBase64String(jpeg), quality, overBound);
  }

  /// <summary>The largest size at or under (w, h) whose longest edge stays at or
  ///     under the cap, never below one pixel.</summary>
  internal static (int W, int H) ScaledTo(int w, int h, int cap)
  {
    if (Math.Max(w, h) <= cap)
    {
      return (w, h);
    }

    double scale = cap / (double)Math.Max(w, h);
    return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
  }

  private static TransformedBitmap Scaled(BitmapSource source, int w, int h, int targetW, int targetH)
  {
    TransformedBitmap scaled = new(source, new ScaleTransform(targetW / (double)w, targetH / (double)h, 0, 0));
    RenderOptions.SetBitmapScalingMode(scaled, BitmapScalingMode.HighQuality);
    scaled.Freeze();
    return scaled;
  }

  private static BitmapFrame Decode(byte[] png)
  {
    PngBitmapDecoder decoder = new(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    return decoder.Frames[0];
  }

  /// <summary>Renders the scaled bitmap over a white rectangle into a fresh pixel
  ///     buffer: the alpha composite (spec 3.5) and a contiguous stride in one pass.
  ///     </summary>
  private static byte[] RasterPixels(TransformedBitmap bmp, int w, int h)
  {
    DrawingVisual visual = new();
    using (DrawingContext dc = visual.RenderOpen())
    {
      dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
      dc.DrawImage(bmp, new Rect(0, 0, w, h));
    }

    RenderTargetBitmap rtb = new(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(visual);
    rtb.Freeze();
    byte[] pixels = new byte[w * h * 4];
    rtb.CopyPixels(pixels, w * 4, 0);
    return pixels;
  }

  /// <summary>Encodes the white-composited BGRA raster as JPEG: an RGB24 copy is
  ///     lossless here because alpha is opaque after the composite.</summary>
  private static byte[] EncodeJpeg(byte[] bgraPixels, int w, int h, int quality)
  {
    int stride = w * 3;
    byte[] rgb = new byte[stride * h];
    for (int row = 0; row < h; row++)
    {
      for (int col = 0; col < w; col++)
      {
        rgb[(row * stride) + (col * 3)] = bgraPixels[(row * w * 4) + (col * 4) + 2];
        rgb[(row * stride) + (col * 3) + 1] = bgraPixels[(row * w * 4) + (col * 4) + 1];
        rgb[(row * stride) + (col * 3) + 2] = bgraPixels[(row * w * 4) + (col * 4)];
      }
    }

    JpegBitmapEncoder encoder = new() { QualityLevel = quality };
    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb24, null, rgb, stride)));
    using MemoryStream ms = new();
    encoder.Save(ms);
    return ms.ToArray();
  }
}
