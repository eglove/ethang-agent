using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using OpenCvSharp;

namespace eThangAgent.Vision.ACL;

/// <summary>Template matching over a raster (OpenCvSharp): finds where a small image
///     appears inside a larger one. This is the primitive that turns a canvas into
///     something addressable - a button, an icon, or a dialog rendered as pixels has no
///     accessibility node, but it does have a shape the model can name once and locate
///     thereafter.
///     <para>Matching runs on grayscale: colour shifts from JPEG re-encoding and theme
///     changes move RGB values far more than they move luminance, so a grayscale
///     comparison is the more stable signal for the same visual shape.</para></summary>
public static class OpenCvTemplateLocator
{
  /// <summary>The normalised-correlation ceiling: a score at or above this is treated as
  ///     a pixel-exact match and reported as 1.0 rather than 0.99999.</summary>
  private const double ExactMatch = 0.999;

  /// <summary>The standard deviation below which a template carries too little variation
  ///     for normalised correlation to be well-conditioned. A flat patch is the degenerate
  ///     case (every position scores 1.0); a nearly-flat one is worse, because it scores
  ///     spuriously high at arbitrary positions. Both are refused rather than answered
  ///     with a confident wrong location.</summary>
  private const double UniformEpsilon = 8.0;

  /// <summary>Locates <paramref name="needle"/> inside <paramref name="raster"/>, returning
  ///     every match at or above <paramref name="minScore"/> best-first. Overlapping
  ///     matches are suppressed so one visual object is reported once rather than as a
  ///     smear of adjacent near-identical scores.</summary>
  public static Result<IReadOnlyList<VisionMatch>> Locate(byte[] raster, byte[] needle, double minScore)
  {
    ArgumentNullException.ThrowIfNull(raster);
    ArgumentNullException.ThrowIfNull(needle);

    using Mat source = Decode(raster);
    if (source.Empty())
    {
      return Result.Failure<IReadOnlyList<VisionMatch>>(
          new DomainError("vision_undecodable_raster", "the raster is not a decodable image."));
    }

    using Mat target = Decode(needle);
    if (target.Empty())
    {
      return Result.Failure<IReadOnlyList<VisionMatch>>(
          new DomainError("vision_undecodable_raster", "the template is not a decodable image."));
    }

    if (target.Width > source.Width || target.Height > source.Height)
    {
      return Result.Failure<IReadOnlyList<VisionMatch>>(new DomainError(
          "vision_template_out_of_range",
          $"the template ({target.Width}x{target.Height}) is larger than the raster ({source.Width}x{source.Height})."));
    }

    using Mat sourceGray = ToGray(source);
    using Mat targetGray = ToGray(target);

    // A uniform template has zero variance, which makes normalised correlation
    // undefined: OpenCV then reports 1.0 at every position, so a blank patch would
    // "match" the entire raster. Such a template carries no shape to find.
    Cv2.MeanStdDev(targetGray, out _, out Scalar deviation);
    if (deviation.Val0 < UniformEpsilon)
    {
      return Result.Failure<IReadOnlyList<VisionMatch>>(new DomainError(
          "vision_template_uniform",
          "the template has no visual variation, so it cannot be located: crop a region that contains a shape, icon, or text."));
    }

    using Mat scores = new();
    Cv2.MatchTemplate(sourceGray, targetGray, scores, TemplateMatchModes.CCoeffNormed);

    return Result.Success<IReadOnlyList<VisionMatch>>(
        CollectMatches(scores, target.Width, target.Height, minScore));
  }

  /// <summary>Walks the score map best-first, emitting each peak and blanking the
  ///     neighbourhood around it so one object yields one match.</summary>
  private static List<VisionMatch> CollectMatches(Mat scores, int width, int height, double minScore)
  {
    List<VisionMatch> matches = [];
    double threshold = Math.Clamp(minScore, 0.0, 1.0);
    using Mat working = scores.Clone();
    while (true)
    {
      Cv2.MinMaxLoc(working, out double min, out double max, out Point minPoint, out Point maxPoint);
      _ = min;
      _ = minPoint;
      if (max < threshold)
      {
        break;
      }

      double reported = max >= ExactMatch ? 1.0 : max;
      matches.Add(new VisionMatch(new VisionRect(maxPoint.X, maxPoint.Y, width, height), reported));

      // Suppress the neighbourhood: a second peak inside the same footprint is the
      // same visual object seen twice, not a second object.
      int left = Math.Max(0, maxPoint.X - (width / 2));
      int top = Math.Max(0, maxPoint.Y - (height / 2));
      int right = Math.Min(working.Width - 1, maxPoint.X + (width / 2));
      int bottom = Math.Min(working.Height - 1, maxPoint.Y + (height / 2));
      Cv2.Rectangle(working, new Rect(left, top, right - left + 1, bottom - top + 1), Scalar.All(0), -1);
    }

    return matches;
  }

  /// <summary>Decodes image bytes into a BGR matrix; an undecodable buffer yields an
  ///     empty matrix rather than an exception, because bad bytes are an expected input
  ///     failure the caller reports as a typed error.</summary>
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

  /// <summary>Single-channel luminance copy of a BGR matrix.</summary>
  private static Mat ToGray(Mat source)
  {
    Mat gray = new();
    if (source.Channels() == 1)
    {
      source.CopyTo(gray);
      return gray;
    }

    Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
    return gray;
  }
}
