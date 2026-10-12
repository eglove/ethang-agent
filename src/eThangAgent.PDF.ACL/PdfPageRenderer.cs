using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using PDFtoImage;
using SkiaSharp;

namespace eThangAgent.PDF.ACL;

/// <summary>The render path over PDFtoImage/PDFium (task 8): stats the file FIRST
///     (issue #117 discipline), gates the open through <see cref="PdfFormatSniffer"/>,
///     resolves the open tail of a page range against the document's page count, then
///     rasterizes each requested 1-based page to PNG at the given DPI. Pages are
///     validated against the count BEFORE any rasterization - an out-of-range page is
///     PageNotFound naming the count, never a clamp. The seam asserts the tool layer's
///     dpi bound defensively (1..MaxDpi). Library faults map to the canonical codes;
///     unknown failures become CorruptPdf with the library message clamped to ~300
///     characters.</summary>
public static class PdfPageRenderer
{
  /// <summary>Rasterizes the listed 1-based pages to PNG images at the given DPI,
  ///     or a typed <see cref="DomainError"/>.</summary>
#pragma warning disable CA1416 // Named decision: the harness is Windows-only; PDFtoImage's platform annotation is advisory and the native PDFium binary ships per-RID inside the package.
  public static Result<PdfRenderOutcome> Render(string path, IReadOnlyList<int> pages, int dpi)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(pages);

    // Defensive: the tool layer validates dpi 1..MaxDpi; the seam asserts the same.
    if (dpi is <= 0 or > PdfLimits.MaxDpi)
    {
      return Fail(ToolErrorCodes.InvalidParameterValue,
          $"dpi must be 1..{PdfLimits.MaxDpi} (got {dpi}); values are never clamped.");
    }

    if (pages.Count > PdfLimits.MaxRenderPagesPerCall)
    {
      return Fail(PdfErrorCodes.InvalidPageRange,
          $"At most {PdfLimits.MaxRenderPagesPerCall} pages may be rendered per call (got {pages.Count}).");
    }

    if (!File.Exists(path))
    {
      return Fail(PdfErrorCodes.FileNotFound, $"File not found: '{path}'.");
    }

    // Stat FIRST: the version is captured before the document is opened. The render
    // outcome carries no version (the tool layer's annotation line is version-free),
    // so the stat is the discipline's own guard.
    _ = new FileInfo(path).Length;

    Result<string> sniff = PdfFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Result.Failure<PdfRenderOutcome>(sniff.Error);
    }

    try
    {
      byte[] bytes = File.ReadAllBytes(path);
      int total = Conversion.GetPageCount(bytes);
      int? bad = pages.Where(page => page < 1 || page > total).Select(page => (int?)page).FirstOrDefault();
      if (bad is int outOfRange)
      {
        return Fail(PdfErrorCodes.PageNotFound,
            $"Page {outOfRange} is out of range; the document has {total} page(s).");
      }

      RenderOptions options = new(Dpi: dpi);
      List<PdfPageImage> images = new(pages.Count);
      foreach (int page in pages)
      {
        using SKBitmap bitmap = Conversion.ToImage(bytes, new Index(page - 1), options: options);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        images.Add(new PdfPageImage(page, data.ToArray(), bitmap.Width, bitmap.Height));
      }

      return Result.Success(new PdfRenderOutcome(images, [.. pages]));
    }
#pragma warning disable CA1031 // Named decision: the render boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      return Fail(PdfErrorCodes.CorruptPdf, $"'{path}' could not be rendered as a PDF: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }
#pragma warning restore CA1416

  /// <summary>Resolves page-range text to the explicit 1-based page list against the
  ///     document's total page count. The open tail 'N-' claims every page from N to
  ///     the last page; a bare 'N' claims only N. A page beyond the count is
  ///     PageNotFound naming the count - never a clamp. The domain parser cannot
  ///     resolve the tail (it has no page count); this is the ACL's resolution point.</summary>
  public static Result<IReadOnlyList<int>> ResolvePageRange(string text, int total)
  {
    ArgumentNullException.ThrowIfNull(text);

    Result<PdfPageRange> parsed = PdfPageRange.Parse(text);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<int>>(parsed.Error);
    }

    string[] parts = text.Split(',');
    List<int> pages = [];
    for (int i = 0; i < parsed.Value.Segments.Count; i++)
    {
      PdfPageSegment segment = parsed.Value.Segments[i];
      bool openTail = parts[i].Trim().EndsWith('-');
      int last = openTail ? total : segment.End ?? segment.Start;
      if (segment.Start > total)
      {
        return PageNotFound(segment.Start, total);
      }

      for (int page = segment.Start; page <= last; page++)
      {
        if (page > total)
        {
          return PageNotFound(page, total);
        }

        pages.Add(page);
      }
    }

    return Result.Success<IReadOnlyList<int>>(pages);
  }

  private static Result<IReadOnlyList<int>> PageNotFound(int page, int total) =>
      Result.Failure<IReadOnlyList<int>>(new DomainError(PdfErrorCodes.PageNotFound,
          $"Page {page} is out of range; the document has {total} page(s)."));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PdfRenderOutcome> Fail(string code, string message)
      => Result.Failure<PdfRenderOutcome>(new DomainError(code, message));
}
