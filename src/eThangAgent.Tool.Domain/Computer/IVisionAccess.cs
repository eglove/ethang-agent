using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A rectangle in delivered-raster pixels: the coordinate space a model
///     reads off a screenshot. Origin is the raster's top-left corner.</summary>
public readonly record struct VisionRect(int X, int Y, int Width, int Height)
{
  /// <summary>The centre of the rectangle, rounded to the nearest pixel: the point a
  ///     click on this region should address.</summary>
  public (int X, int Y) Centre => (X + (Width / 2), Y + (Height / 2));
}

/// <summary>One template match: where the template was found and how strongly.
///     <see cref="Score"/> is normalised to 0..1, where 1 is a pixel-exact match.</summary>
public sealed record VisionMatch(VisionRect Bounds, double Score);

/// <summary>One recognised word and the raster region it occupies, so a model can
///     turn text it can read into a coordinate it can click.</summary>
public sealed record VisionWord(string Text, VisionRect Bounds, double Confidence);

/// <summary>The typed failures of a vision request. Every case is a value the model
///     can act on: a bad template is a different correction from an unreadable raster.
///     </summary>
#pragma warning disable CA1034 // Named decision: the failure discriminators ARE the contract (the spec's closed failure set); public nested cases keep the case names as declared.
public abstract record VisionFailure
{
  /// <summary>The raster bytes are not a decodable image.</summary>
  public sealed record UndecodableRaster(string Detail) : VisionFailure;

  /// <summary>The template is larger than the raster it is searched in, or empty.</summary>
  public sealed record TemplateOutOfRange(string Detail) : VisionFailure;

  /// <summary>The OCR engine could not be initialised (its language data is missing).</summary>
  public sealed record OcrUnavailable(string Detail) : VisionFailure;

  /// <summary>The requested region lies outside the raster.</summary>
  public sealed record RegionOutOfRange(string Detail) : VisionFailure;
}
#pragma warning restore CA1034

/// <summary>The Vision seam: locating a template inside a raster and reading text out
///     of one. A canvas surface (a remote desktop, a video frame, a drawing) exposes no
///     accessibility tree, so pixels are the only evidence it offers; this seam turns
///     those pixels into coordinates and words the model can act on. The Tool Domain
///     never knows OpenCV or Tesseract exist.</summary>
public interface IVisionAccess
{
  /// <summary>Finds <paramref name="needle"/> inside <paramref name="raster"/>. Returns
  ///     every match at or above <paramref name="minScore"/> (0..1), best first; an empty
  ///     list is a successful search that found nothing.</summary>
  Result<IReadOnlyList<VisionMatch>> Locate(byte[] raster, byte[] needle, double minScore,
      CancellationToken ct = default);

  /// <summary>Reads the words in <paramref name="raster"/>, optionally restricted to one
  ///     region of it. Bounds are reported in the FULL raster's pixel space, so a region
  ///     read and a whole-raster read agree on coordinates.</summary>
  Result<IReadOnlyList<VisionWord>> ReadText(byte[] raster, VisionRect? region,
      CancellationToken ct = default);
}
