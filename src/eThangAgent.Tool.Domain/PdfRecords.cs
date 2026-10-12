#pragma warning disable CA1819 // Named decision: PngBytes is the delivered raster buffer, one allocation handed over, never mutated after; a copied collection would double the allocation per rendered page.
namespace eThangAgent.ToolDomain;

/// <summary>One page of extracted text: its 1-based page number, the page's media-box
///     dimensions in points, and the page's text lines in reading order.</summary>
public sealed record PdfPageText(int PageNumber, double WidthPt, double HeightPt, IReadOnlyList<string> Lines);

/// <summary>The PDF read snapshot: the file's content-true version at the read moment,
///     the document's total page count, the requested offset, the requested page slice,
///     and the document metadata. Read surfaces the version; manipulations require it
///     back.</summary>
public sealed record PdfSnapshot(
    FileVersion Version,
    int TotalPages,
    int Offset,
    IReadOnlyList<PdfPageText> Pages,
    string? Title,
    string? Author);

/// <summary>One rendered page: its 1-based page number, the PNG bytes, and the raster
///     dimensions in pixels.</summary>
public sealed record PdfPageImage(int PageNumber, byte[] PngBytes, int WidthPx, int HeightPx);

/// <summary>The result of a render call: the rendered page images and the 1-based page
///     numbers that were rendered (in request order).</summary>
public sealed record PdfRenderOutcome(IReadOnlyList<PdfPageImage> Images, IReadOnlyList<int> RenderedPages);

/// <summary>The result of a create: the new file's content-true version and its page
///     count.</summary>
public sealed record PdfSaveOutcome(FileVersion Version, int PageCount);

/// <summary>The result of an atomic manipulate batch: the file's new content-true
///     version, its page count, and how many operations were applied.</summary>
public sealed record PdfEditOutcome(FileVersion NewVersion, int PageCount, int OperationsApplied);

/// <summary>One edit operation inside an atomic batch. All operations are validated and
///     resolved BEFORE any mutation; the first invalid op refuses the whole batch. The
///     marker record is deliberately empty: it exists to type the closed operation
///     vocabulary (the DocxOperation pattern), not to carry members. All page numbers
///     are 1-based.</summary>
#pragma warning disable S2094 // Named decision: deliberate empty marker record typing the closed op vocabulary.
public abstract record PdfOperation;
#pragma warning restore S2094

/// <summary>Rotates the listed pages by the given degrees (a multiple of 90).</summary>
public sealed record RotatePages(IReadOnlyList<int> Pages, int Degrees) : PdfOperation;

/// <summary>Deletes the listed pages.</summary>
public sealed record DeletePages(IReadOnlyList<int> Pages) : PdfOperation;

/// <summary>Reorders the whole document to the given 1-based page order (a permutation
///     of every page).</summary>
public sealed record ReorderPages(IReadOnlyList<int> Order) : PdfOperation;

/// <summary>Extracts the listed pages into a new document at OutputPath.</summary>
public sealed record ExtractPages(IReadOnlyList<int> Pages, string OutputPath) : PdfOperation;

/// <summary>Merges the listed source documents into this one.</summary>
public sealed record Merge(IReadOnlyList<string> Sources) : PdfOperation;

/// <summary>Stamps a text watermark on every page. Null members use the ACL defaults
///     (font size, opacity, rotation).</summary>
public sealed record WatermarkText(string Text, double? FontSize, double? Opacity, int? Degrees) : PdfOperation;

/// <summary>Sets document metadata. Null members are left unchanged.</summary>
public sealed record SetMetadata(string? Title, string? Author, string? Subject) : PdfOperation;
