using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Vision.ACL;

/// <summary>The Vision seam over OpenCV and Tesseract: template matching and OCR for
///     surfaces that expose pixels instead of an accessibility tree. The two engines
///     stay behind this class, so the Tool Domain sees only rects, words, and scores.
///     </summary>
public sealed class VisionAccess : IVisionAccess
{
  /// <inheritdoc />
  public Result<IReadOnlyList<VisionMatch>> Locate(byte[] raster, byte[] needle, double minScore,
      CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    return OpenCvTemplateLocator.Locate(raster, needle, minScore);
  }

  /// <inheritdoc />
  public Result<IReadOnlyList<VisionWord>> ReadText(byte[] raster, VisionRect? region,
      CancellationToken ct = default)
  {
    ct.ThrowIfCancellationRequested();
    return TesseractTextReader.ReadText(raster, region);
  }
}
