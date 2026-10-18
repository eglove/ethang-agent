using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace eThangAgent.PDF.ACL;

/// <summary>The outcome of one applied manipulate batch: the working document's page
///     count after every operation ran and how many operations were applied.</summary>
public sealed record PdfManipulatorOutcome(int PageCount, int OperationsApplied);

/// <summary>The manipulate path (task 7): applies a batch of structural operations to an
///     in-memory working copy over PDFsharp 6, then writes the result atomically (temp
///     file in the same directory, then <see cref="File.Move(string, string, bool)"/> over
///     the target). EVERY operation is validated against the running page count BEFORE
///     any mutation - page references out of range fail PageNotFound naming the operation
///     index, and a reorder that is not a full permutation of 1..N fails InvalidPageRange -
///     so one invalid op refuses the whole batch with nothing saved. Library faults map to
///     CorruptPdf - never an exception.</summary>
public static class PdfManipulator
{
  private const double DefaultWatermarkFontSize = 48;
  private const double DefaultWatermarkOpacity = 0.3;
  private const int DefaultWatermarkDegrees = 45;

  // Windows-only harness: the PDFsharp 6.x core build's built-in
  // WindowsPlatformFontResolver resolves standard faces (Arial/Times) from
  // C:\Windows\Fonts. No add-on resolver package is required for the core flavor.
  static PdfManipulator() => GlobalFontSettings.UseWindowsFontsUnderWindows = true;

  /// <summary>Applies <paramref name="operations"/> to the PDF at <paramref name="path"/>
  ///     atomically, or a typed <see cref="DomainError"/> with nothing saved.</summary>
  public static Result<PdfManipulatorOutcome> Apply(string path, IReadOnlyList<PdfOperation> operations)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(operations);

    List<(string OutputPath, IReadOnlyList<int> Pages)> extracts = [];
    string temp = TempPathFor(path);
    try
    {
      int pageCount;
      using (PdfDocument doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
      {
        // Validate EVERY operation against the running page count BEFORE any mutation.
        Result<bool> validated = Validate(doc.PageCount, operations);
        if (!validated.IsSuccess)
        {
          return Result.Failure<PdfManipulatorOutcome>(validated.Error);
        }

        for (int i = 0; i < operations.Count; i++)
        {
          ApplyOperation(doc, operations[i], extracts);
        }

        pageCount = doc.PageCount;
        doc.Save(temp);
      }

      File.Move(temp, Path.GetFullPath(path), true);

      // Extraction runs only after the target itself is in place, so a failure leaves no
      // stray output. It reads the resulting document (PDFsharp's Save is one-shot, so no
      // mid-batch snapshot exists); the running-count validation above already guarantees
      // the requested pages exist at the extract op's point in the batch.
      foreach ((string outputPath, IReadOnlyList<int> pages) in extracts)
      {
        ExtractToFile(path, pages, outputPath);
      }

      return Result.Success(new PdfManipulatorOutcome(pageCount, operations.Count));
    }
#pragma warning disable CA1031 // Named decision: the open/apply/save boundary maps every library fault to a typed DomainError; an unknown failure must never escape as an exception.
    catch (Exception ex)
    {
      TryDelete(temp);
      return Fail(PdfErrorCodes.CorruptPdf, $"'{path}' could not be manipulated: {Clamp(ex.Message)}");
    }
#pragma warning restore CA1031
  }

  /// <summary>Walks the batch validating each operation against the page count as it
  ///     stands at that point in the sequence (a delete shrinks it, a merge grows it),
  ///     mutating nothing. The first violation is returned with its operation index.</summary>
  private static Result<bool> Validate(int pageCount, IReadOnlyList<PdfOperation> operations)
  {
    int count = pageCount;
    for (int i = 0; i < operations.Count; i++)
    {
      if (ValidateOne(i, operations[i], ref count) is { } failure)
      {
        return failure;
      }
    }

    return Result.Success(true);
  }

  /// <summary>Validates one operation against the running page count, updating
  ///     <paramref name="count"/> for page-count-mutating operations (delete shrinks,
  ///     merge grows); a non-null result is the batch's first violation.</summary>
  private static Result<bool>? ValidateOne(int index, PdfOperation operation, ref int count)
  {
    switch (operation)
    {
      case RotatePages rotate:
        return ValidateRotate(index, rotate, count);

      case DeletePages delete:
        if (ValidateDelete(index, delete, count) is { } deleteFailure)
        {
          return deleteFailure;
        }

        int current = count;
        count = current - delete.Pages.Distinct().Count(page => page >= 1 && page <= current);
        return null;

      case ReorderPages reorder:
        return ValidateReorder(index, reorder, count);

      case ExtractPages extract:
        return ValidateExtract(index, extract, count);

      case Merge merge:
        return ValidateMerge(index, merge, ref count);

      case WatermarkText:
      case SetMetadata:
        return null;

      default:
        return FailOp(index, "unknown", PdfErrorCodes.InvalidPageRange,
            $"unsupported operation type '{operation.GetType().Name}'.");
    }
  }


  private static Result<bool>? ValidateReorder(int index, ReorderPages reorder, int count)
  {
    if (IsFullPermutation(reorder.Order, count))
    {
      return null;
    }

    string plural = reorder.Order.Count == 1 ? "y" : "ies";
    return FailOp(index, "reorderPages", PdfErrorCodes.InvalidPageRange,
        $"'order' must be a full permutation of 1..{count} (got {reorder.Order.Count} entr{plural}).");
  }

  private static Result<bool>? ValidateRotate(int index, RotatePages rotate, int count)
  {
    int? bad = FirstOutOfRange(rotate.Pages, count);
    Result<bool>? badPage = bad is not null
      ? FailOp(index, "rotate", PdfErrorCodes.PageNotFound, PageOutOfRangeMessage(bad.Value, count))
      : null;
    return badPage ?? (rotate.Degrees % 90 != 0
      ? FailOp(index, "rotate", PdfErrorCodes.InvalidPageRange, $"'degrees' must be a multiple of 90 (got {rotate.Degrees}).")
      : null);
  }

  private static Result<bool>? ValidateDelete(int index, DeletePages delete, int count)
  {
    int? bad = FirstOutOfRange(delete.Pages, count);
    Result<bool>? badPage = bad is not null
      ? FailOp(index, "deletePages", PdfErrorCodes.PageNotFound, PageOutOfRangeMessage(bad.Value, count))
      : null;
    int remaining = count - delete.Pages.Distinct().Count(page => page >= 1 && page <= count);
    return badPage ?? (remaining < 1
      ? FailOp(index, "deletePages", PdfErrorCodes.InvalidPageRange,
          $"deleting {delete.Pages.Count} page(s) would remove every page of {count}.")
      : null);
  }

  private static Result<bool>? ValidateExtract(int index, ExtractPages extract, int count)
  {
    int? bad = FirstOutOfRange(extract.Pages, count);
    Result<bool>? badPage = bad is not null
      ? FailOp(index, "extractPages", PdfErrorCodes.PageNotFound, PageOutOfRangeMessage(bad.Value, count))
      : null;
    return badPage ?? (File.Exists(extract.OutputPath)
      ? FailOp(index, "extractPages", PdfErrorCodes.OutputExists, $"output file already exists: '{extract.OutputPath}'.")
      : null);
  }

  private static Result<bool>? ValidateMerge(int index, Merge merge, ref int count)
  {
    foreach (string source in merge.Sources)
    {
      if (!File.Exists(source))
      {
        return FailOp(index, "merge", PdfErrorCodes.FileNotFound, $"source file not found: '{source}'.");
      }

      Result<string> sniff = PdfFormatSniffer.Inspect(source);
      if (!sniff.IsSuccess)
      {
        return FailOp(index, "merge", sniff.Error.Code, $"source '{source}': {sniff.Error.Message}");
      }

      count += SourcePageCount(source);
    }

    return null;
  }

  /// <summary>Applies one validated operation to the working document. Extraction is
  ///     deferred: the new document is built in memory and saved only after the target
  ///     itself has been written, so a later failure leaves no stray output.</summary>
  private static void ApplyOperation(PdfDocument doc, PdfOperation operation, List<(string, IReadOnlyList<int>)> extracts)
  {
    switch (operation)
    {
      case RotatePages rotate:
        foreach (int page in rotate.Pages)
        {
          doc.Pages[page - 1].Rotate = rotate.Degrees;
        }

        break;

      case DeletePages delete:
        foreach (int page in delete.Pages.Distinct().OrderByDescending(p => p))
        {
          doc.Pages.RemoveAt(page - 1);
        }

        break;

      case ReorderPages reorder:
        Reorder(doc, reorder.Order);
        break;

      case ExtractPages extract:
        extracts.Add((extract.OutputPath, extract.Pages));
        break;

      case Merge merge:
        foreach (string source in merge.Sources)
        {
          MergeSource(doc, source);
        }

        break;

      case WatermarkText watermark:
        Watermark(doc, watermark);
        break;

      case SetMetadata metadata:
        ApplyMetadata(doc, metadata);
        break;

      default:
        break;
    }
  }

  /// <summary>Builds a new document holding the requested pages of the SOURCE document at
  ///     <paramref name="sourcePath"/> (opened in Import mode, which is what PDFsharp
  ///     requires to import pages) and writes it to <paramref name="outputPath"/>.</summary>
  private static void ExtractToFile(string sourcePath, IReadOnlyList<int> pages, string outputPath)
  {
    using PdfDocument sourceDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
    using PdfDocument extracted = new();
    foreach (int page in pages)
    {
      _ = extracted.AddPage(sourceDoc.Pages[page - 1]);
    }

    SaveNewFile(extracted, outputPath);
  }

  /// <summary>Rewrites the page sequence to the given 1-based order using PDFsharp's
  ///     <see cref="PdfPages.MovePage(int, int)"/> (the sanctioned in-place move; detach
  ///     and re-add trips the object table). The order is validated as a full permutation
  ///     beforehand.</summary>
  private static void Reorder(PdfDocument doc, IReadOnlyList<int> order)
  {
    List<int> current = [.. Enumerable.Range(1, doc.PageCount)];
    for (int position = 0; position < order.Count; position++)
    {
      int from = current.IndexOf(order[position]);
      if (from == position)
      {
        continue;
      }

      doc.Pages.MovePage(from, position);
      int moved = current[from];
      current.RemoveAt(from);
      current.Insert(position, moved);
    }
  }

  /// <summary>Appends every page of <paramref name="source"/> to the working document.</summary>
  private static void MergeSource(PdfDocument doc, string source)
  {
    using PdfDocument sourceDoc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
    doc.Pages.InsertRange(doc.PageCount, sourceDoc, 0, sourceDoc.PageCount);
  }

  /// <summary>Stamps a diagonal text watermark on every page: the graphics state is
  ///     saved, translated to the page centre, rotated, and the text drawn centred;
  ///     opacity rides the brush colour's alpha channel.</summary>
  private static void Watermark(PdfDocument doc, WatermarkText watermark)
  {
    double fontSize = watermark.FontSize ?? DefaultWatermarkFontSize;
    double opacity = watermark.Opacity ?? DefaultWatermarkOpacity;
    int degrees = watermark.Degrees ?? DefaultWatermarkDegrees;
    int alpha = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

    XFont font = new("Arial", fontSize);
    XBrush brush = new XSolidBrush(XColor.FromArgb(alpha, 0, 0, 0));
    foreach (PdfPage page in doc.Pages)
    {
      using XGraphics gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
      double width = page.Width.Point;
      double height = page.Height.Point;
      XGraphicsState state = gfx.Save();
      gfx.TranslateTransform(width / 2, height / 2);
      gfx.RotateTransform(-degrees);
      gfx.TranslateTransform(-width / 2, -height / 2);
      gfx.DrawString(watermark.Text, font, brush, new XRect(0, 0, width, height), XStringFormats.Center);
      gfx.Restore(state);
    }
  }

  /// <summary>Sets the document information fields; a null member is left unchanged.</summary>
  private static void ApplyMetadata(PdfDocument doc, SetMetadata metadata)
  {
    if (metadata.Title is not null)
    {
      doc.Info.Title = metadata.Title;
    }

    if (metadata.Author is not null)
    {
      doc.Info.Author = metadata.Author;
    }

    if (metadata.Subject is not null)
    {
      doc.Info.Subject = metadata.Subject;
    }
  }

  private static int SourcePageCount(string source)
  {
    using PdfDocument doc = PdfReader.Open(source, PdfDocumentOpenMode.Import);
    return doc.PageCount;
  }

  private static int? FirstOutOfRange(IReadOnlyList<int> pages, int count)
  {
    foreach (int page in pages)
    {
      if (page < 1 || page > count)
      {
        return page;
      }
    }

    return null;
  }

  private static bool IsFullPermutation(IReadOnlyList<int> order, int count)
  {
    if (order.Count != count)
    {
      return false;
    }

    bool[] seen = new bool[count + 1];
    foreach (int page in order)
    {
      if (page < 1 || page > count || seen[page])
      {
        return false;
      }

      seen[page] = true;
    }

    return true;
  }

  /// <summary>Writes a new document to a temp file in the target's directory, then moves
  ///     it into place - a crash mid-write never leaves a half-written document.</summary>
  private static void SaveNewFile(PdfDocument document, string outputPath)
  {
    string full = Path.GetFullPath(outputPath);
    string temp = TempPathFor(full);
    try
    {
      document.Save(temp);
    }
#pragma warning disable CA1031 // Named decision: clean up the temp file on any save fault, then rethrow into the caller's typed-error boundary.
    catch (Exception)
    {
      TryDelete(temp);
      throw;
    }
#pragma warning restore CA1031

    File.Move(temp, full, false);
  }

  private static string TempPathFor(string path)
  {
    string full = Path.GetFullPath(path);
    string dir = Path.GetDirectoryName(full) ?? ".";
    return Path.Combine(dir, Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (IOException)
    {
      // best-effort cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort cleanup
    }
  }

  private static string PageOutOfRangeMessage(int page, int count)
    => $"page {page} is out of range; the document has {count} page(s).";

  private static Result<bool> FailOp(int index, string opName, string code, string message)
    => Result.Failure<bool>(new DomainError(code, $"operations[{index}] ({opName}): {message}"));

  private static string Clamp(string message)
  {
    const int cap = 300;
    return message.Length <= cap ? message : message[..cap] + "...";
  }

  private static Result<PdfManipulatorOutcome> Fail(string code, string message)
    => Result.Failure<PdfManipulatorOutcome>(new DomainError(code, message));
}
