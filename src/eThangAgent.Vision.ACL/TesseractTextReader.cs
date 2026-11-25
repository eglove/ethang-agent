using System.Reflection;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using OpenCvSharp;
using Tesseract;
using TesseractRect = Tesseract.Rect;

namespace eThangAgent.Vision.ACL;

/// <summary>Reads text out of a raster (Tesseract): the other half of making a canvas
///     addressable. Template matching finds a shape the model already has an image of;
///     OCR reads text the model has never seen, which is what turns a remote desktop's
///     menu, dialog, or console output into something the model can act on.
///     <para>Every word carries its bounding box in the FULL raster's pixel space, so a
///     region read and a whole-raster read agree on coordinates and a word can be
///     clicked directly.</para></summary>
public static class TesseractTextReader
{
  /// <summary>The language the packaged data covers.</summary>
  private const string Language = "eng";

  /// <summary>Reads the words in <paramref name="raster"/>, restricted to
  ///     <paramref name="region"/> when given. Bounds are in the full raster's space.
  ///     </summary>
  public static Result<IReadOnlyList<VisionWord>> ReadText(byte[] raster, VisionRect? region)
  {
    ArgumentNullException.ThrowIfNull(raster);

    using Mat source = Decode(raster);
    if (source.Empty())
    {
      return Result.Failure<IReadOnlyList<VisionWord>>(
          new DomainError("vision_undecodable_raster", "the raster is not a decodable image."));
    }

    (int offsetX, int offsetY) = (0, 0);
    using Mat crop = Crop(source, region, out bool cropped);
    if (!cropped)
    {
      return Result.Failure<IReadOnlyList<VisionWord>>(new DomainError(
          "vision_region_out_of_range",
          $"the region lies outside the {source.Width}x{source.Height} raster."));
    }

    if (region is { } r)
    {
      (offsetX, offsetY) = (r.X, r.Y);
    }

    using Mat prepared = Prepare(crop, out double scale);
    _ = Cv2.ImEncode(".png", prepared, out byte[] png);

    try
    {
      using TesseractEngine engine = new(TessdataPath(), Language, EngineMode.Default);
      using Pix pix = Pix.LoadFromMemory(png);
      using Page page = engine.Process(pix);
      return Result.Success<IReadOnlyList<VisionWord>>(Words(page, offsetX, offsetY, scale));
    }
    catch (TesseractException ex)
    {
      return Result.Failure<IReadOnlyList<VisionWord>>(
          new DomainError("vision_ocr_unavailable", $"the OCR engine could not read the raster: {ex.Message}"));
    }
  }

  /// <summary>Walks the page's words, lifting each box into full-raster coordinates.
  ///     The engine reports boxes in the PREPARED image's space, which was upscaled, so
  ///     each box is divided by that factor before the region offset is added - without
  ///     this the reported position is scaled away from where the text actually is.</summary>
  private static List<VisionWord> Words(Page page, int offsetX, int offsetY, double scale)
  {
    List<VisionWord> words = [];
    using ResultIterator iterator = page.GetIterator();
    iterator.Begin();
    do
    {
      if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out TesseractRect box))
      {
        continue;
      }

      string? text = iterator.GetText(PageIteratorLevel.Word);
      if (string.IsNullOrWhiteSpace(text))
      {
        continue;
      }

      int x = (int)Math.Round(box.X1 / scale) + offsetX;
      int y = (int)Math.Round(box.Y1 / scale) + offsetY;
      int width = (int)Math.Round(box.Width / scale);
      int height = (int)Math.Round(box.Height / scale);
      words.Add(new VisionWord(
          text.Trim(),
          new VisionRect(x, y, width, height),
          iterator.GetConfidence(PageIteratorLevel.Word) / 100.0));
    }
    while (iterator.Next(PageIteratorLevel.Word));

    return words;
  }

  /// <summary>Crops to the requested region; a null region copies the whole raster.
  ///     False when the region does not fit inside the source.</summary>
  private static Mat Crop(Mat source, VisionRect? region, out bool ok)
  {
    ok = true;
    if (region is not { } r)
    {
      return source.Clone();
    }

    if (r.Width <= 0 || r.Height <= 0 || r.X < 0 || r.Y < 0
        || r.X + r.Width > source.Width || r.Y + r.Height > source.Height)
    {
      ok = false;
      return new Mat();
    }

    using Mat view = new(source, new OpenCvSharp.Rect(r.X, r.Y, r.Width, r.Height));
    return view.Clone();
  }

  /// <summary>Grayscale plus Otsu binarisation: a screenshot's text is dark on light or
  ///     light on dark, and Otsu picks the split without being told which. Upscaling a
  ///     small crop matters more than any other knob - Tesseract's model is trained on
  ///     text far larger than a UI's 11px labels.</summary>
  private static Mat Prepare(Mat crop, out double scale)
  {
    using Mat gray = new();
    if (crop.Channels() == 1)
    {
      crop.CopyTo(gray);
    }
    else
    {
      Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
    }

    Mat scaled = new();
    scale = gray.Height < MinHeight ? MinHeight / (double)gray.Height : 1.0;
    Cv2.Resize(gray, scaled, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);

    Mat binary = new();
    _ = Cv2.Threshold(scaled, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
    scaled.Dispose();
    return binary;
  }

  /// <summary>The height below which a crop is upscaled before recognition.</summary>
  private const int MinHeight = 400;

  /// <summary>The packaged language data's directory: it travels with the assembly as a
  ///     package asset, so nothing is written to the user's machine.</summary>
  private static string TessdataPath()
  {
    string assembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
    return Path.Combine(assembly, "tessdata");
  }

  /// <summary>Decodes image bytes into a BGR matrix; bad bytes yield an empty matrix,
  ///     because undecodable input is an expected failure the caller reports typed.
  ///     </summary>
  private static Mat Decode(byte[] bytes)
  {
    try
    {
      return Cv2.ImDecode(bytes, ImreadModes.Color);
    }
    catch (OpenCVException)
    {
      return new Mat();
    }
  }
}
