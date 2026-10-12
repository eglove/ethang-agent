using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Canonical <see cref="DomainError"/> codes for the PDF surface (task 1):
///     the only codes an <see cref="IPdfAccess"/> failure may carry. The tool renders
///     them as standard 'Error [Code]: message' blocks; the ACL is the only producer.
///     </summary>
public static class PdfErrorCodes
{
  /// <summary>The file's magic bytes are not a PDF (%PDF- header absent).</summary>
  public const string NotAPdf = "NotAPdf";

  /// <summary>The document is encrypted and cannot be opened without a password.</summary>
  public const string EncryptedPdf = "EncryptedPdf";

  /// <summary>The document opens but is unreadable (malformed xref, truncated, damaged).</summary>
  public const string CorruptPdf = "CorruptPdf";

  /// <summary>The file exceeds the size cap (100 MB; PdfLimits.MaxFileBytes in the ACL).</summary>
  public const string OversizePdf = "OversizePdf";

  /// <summary>A requested page number resolves to no page in the document.</summary>
  public const string PageNotFound = "PageNotFound";

  /// <summary>The page-range text violates the pinned grammar (see <see cref="PdfPageRange"/>).</summary>
  public const string InvalidPageRange = "InvalidPageRange";

  /// <summary>The output path already names an existing file (create/extract refuse to overwrite).</summary>
  public const string OutputExists = "OutputExists";

  /// <summary>The expectedVersion the caller supplied does not match the file's current
  ///     version — the file changed under the model since its read.</summary>
  public const string StaleDocument = "StaleDocument";

  /// <summary>The create input violates the pinned markdown subset.</summary>
  public const string MarkdownInvalid = "MarkdownInvalid";

  /// <summary>The path does not name an existing file.</summary>
  public const string FileNotFound = "FileNotFound";
}
