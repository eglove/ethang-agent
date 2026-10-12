using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The PDF seam (task 1): the domain knows nothing about PDF libraries;
///     implementations translate. All paths are pre-resolved workspace paths.
///     Every method stats the file FIRST and returns the content-true
///     <see cref="FileVersion"/> (issue #117 pattern) — the version the read surfaces
///     and the manipulations require back. Expected failures flow as typed
///     <see cref="DomainError"/> values carrying only <see cref="PdfErrorCodes"/> codes —
///     never exceptions.</summary>
public interface IPdfAccess
{
  /// <summary>Reads one page slice of extracted text plus document metadata. Page
  ///     numbers are 1-based; <paramref name="offset"/> is the 0-based first page
  ///     (offset 0 names page 1) and <paramref name="maxPages"/> caps the slice (the
  ///     ACL refuses an offset at or past the page count with PageNotFound — never a
  ///     clamp).</summary>
  Task<Result<PdfSnapshot>> ReadAsync(string path, int offset, int maxPages, CancellationToken ct = default);

  /// <summary>Renders the listed 1-based pages to PNG images at the given DPI.</summary>
  Task<Result<PdfRenderOutcome>> RenderAsync(string path, IReadOnlyList<int> pages, int dpi, CancellationToken ct = default);

  /// <summary>Creates a new PDF from the pinned markdown subset (fails MarkdownInvalid
  ///     when the input violates it; fails OutputExists when the target file already
  ///     exists).</summary>
  Task<Result<PdfSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default);

  /// <summary>Applies an atomic manipulate batch: all operations are validated and
  ///     resolved BEFORE any mutation; the first invalid op refuses the whole batch with
  ///     nothing saved. <paramref name="expectedVersion"/> must match the file's current
  ///     version (StaleDocument otherwise).</summary>
  Task<Result<PdfEditOutcome>> ManipulateAsync(string path, FileVersion expectedVersion, IReadOnlyList<PdfOperation> operations, CancellationToken ct = default);
}
