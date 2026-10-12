using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PDF.ACL;

/// <summary>The <see cref="IPdfAccess"/> implementation: PdfPig for the read path
///     (task 5), QuestPDF for the create path (task 6), PDFsharp for the manipulate
///     path (task 7), and PDFtoImage/PDFium for the render path (task 8). Every method
///     stats the file FIRST and returns the content-true <see cref="FileVersion"/>
///     (issue #117 pattern); the sniffer gates every open.</summary>
public sealed class DirectPdfAccess : IPdfAccess
{
  /// <summary>Reads one page slice of extracted text plus document metadata (task 5).</summary>
  public Task<Result<PdfSnapshot>> ReadAsync(string path, int offset, int maxPages, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    return Task.FromResult((offset < 0 || maxPages is < 1 or > PdfLimits.MaxPagesPerRead)
      ? FailSnapshot(ToolErrorCodes.InvalidParameterValue,
        $"offset must be >= 0 and maxPages must be 1..{PdfLimits.MaxPagesPerRead}; got offset {offset}, maxPages {maxPages}.")
      : PdfTextExtractor.Extract(path, offset, maxPages));
  }

  /// <summary>Renders the listed 1-based pages to PNG images at the given DPI (task 8).
  ///     The page list arrives ALREADY resolved to explicit page numbers (the tool layer
  ///     owns the grammar and the open-tail resolution against the document's page count);
  ///     the seam stats the file FIRST, gates the open through the sniffer, validates
  ///     every page against the count, and rasterizes each page via
  ///     <see cref="PdfPageRenderer"/>. The version the stat captures is surfaced by the
  ///     TOOL layer's annotation line, never inside the images.</summary>
  public Task<Result<PdfRenderOutcome>> RenderAsync(string path, IReadOnlyList<int> pages, int dpi, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(pages);
    return Task.FromResult(PdfPageRenderer.Render(path, pages, dpi));
  }

  /// <summary>Creates a new PDF from the pinned markdown subset (task 6). The target
  ///     must not already exist (OutputExists) and its directory must exist
  ///     (FileNotFound); the composer writes the file, then the new file is stat'd
  ///     for the content-true version and its page count is reported.</summary>
  public Task<Result<PdfSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(markdown);
    if (File.Exists(path))
    {
      return Task.FromResult(Result.Failure<PdfSaveOutcome>(new DomainError(PdfErrorCodes.OutputExists,
          $"Output file already exists: '{path}'.")));
    }

    string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (dir is null || !Directory.Exists(dir))
    {
      return Task.FromResult(Result.Failure<PdfSaveOutcome>(new DomainError(PdfErrorCodes.FileNotFound,
          $"Directory not found: '{dir}'.")));
    }

    Result<int> composed = PdfComposer.Compose(markdown, path);
    if (!composed.IsSuccess)
    {
      return Task.FromResult(Result.Failure<PdfSaveOutcome>(composed.Error));
    }

    FileInfo info = new(path);
    FileVersion version = new(info.Length, info.LastWriteTimeUtc);
    return Task.FromResult(Result.Success(new PdfSaveOutcome(version, composed.Value)));
  }

  /// <summary>Applies an atomic manipulate batch (task 7): stats the file FIRST for the
  ///     content-true version (StaleDocument when <paramref name="expectedVersion"/>
  ///     differs), gates the open through the sniffer, validates EVERY operation against
  ///     the page count BEFORE any mutation, applies them to an in-memory working copy,
  ///     and writes the result atomically. One invalid op refuses the whole batch with
  ///     nothing saved. Extract output paths arrive pre-resolved (the tool layer owns
  ///     workspace containment); the seam only enforces OutputExists.</summary>
  public Task<Result<PdfEditOutcome>> ManipulateAsync(string path, FileVersion expectedVersion, IReadOnlyList<PdfOperation> operations, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(expectedVersion);
    ArgumentNullException.ThrowIfNull(operations);

    if (!File.Exists(path))
    {
      return Task.FromResult(Result.Failure<PdfEditOutcome>(new DomainError(PdfErrorCodes.FileNotFound,
          $"File not found: '{path}'.")));
    }

    // Stat FIRST: the version is the pair captured before the document is opened.
    FileInfo info = new(path);
    FileVersion current = new(info.Length, info.LastWriteTimeUtc);
    if (current != expectedVersion)
    {
      return Task.FromResult(Result.Failure<PdfEditOutcome>(new DomainError(PdfErrorCodes.StaleDocument,
          $"'{path}' changed since it was read (expected {expectedVersion.Token}, found {current.Token}).")));
    }

    Result<string> sniff = PdfFormatSniffer.Inspect(path);
    if (!sniff.IsSuccess)
    {
      return Task.FromResult(Result.Failure<PdfEditOutcome>(sniff.Error));
    }

    Result<PdfManipulatorOutcome> applied = PdfManipulator.Apply(path, operations);
    if (!applied.IsSuccess)
    {
      return Task.FromResult(Result.Failure<PdfEditOutcome>(applied.Error));
    }

    FileInfo after = new(path);
    FileVersion newVersion = new(after.Length, after.LastWriteTimeUtc);
    return Task.FromResult(Result.Success(new PdfEditOutcome(newVersion, applied.Value.PageCount, applied.Value.OperationsApplied)));
  }

  private static Result<PdfSnapshot> FailSnapshot(string code, string message)
      => Result.Failure<PdfSnapshot>(new DomainError(code, message));
}
