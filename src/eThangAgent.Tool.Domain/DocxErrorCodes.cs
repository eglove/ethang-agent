using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Canonical <see cref="DomainError"/> codes for the docx surface (task 1):
///     the only codes an <see cref="IDocxAccess"/> failure may carry. The tool renders
///     them as standard 'Error [Code]: message' blocks; the ACL is the only producer.
///     </summary>
public static class DocxErrorCodes
{
  /// <summary>The file's magic bytes are not a zip package.</summary>
  public const string NotADocx = "NotADocx";

  /// <summary>A vbaProject part is present (.docm content) — macro documents are refused.</summary>
  public const string MacroDocument = "MacroDocument";

  /// <summary>The package opens but is unreadable (missing/corrupt document part).</summary>
  public const string CorruptDocument = "CorruptDocument";

  /// <summary>The file exceeds the size cap (DocxLimits.MaxFileBytes).</summary>
  public const string OversizeDocument = "OversizeDocument";

  /// <summary>The expectedVersion the caller supplied does not match the file's current
  ///     version — the file changed under the model since its read.</summary>
  public const string StaleDocument = "StaleDocument";

  /// <summary>The address is grammatically valid but resolves to no block.</summary>
  public const string AddressNotFound = "AddressNotFound";

  /// <summary>The address text violates the pinned grammar.</summary>
  public const string InvalidAddress = "InvalidAddress";

  /// <summary>The paragraph style name is unknown (the message lists the available styles).</summary>
  public const string StyleNotFound = "StyleNotFound";

  /// <summary>The comment id resolves to no comment.</summary>
  public const string CommentNotFound = "CommentNotFound";

  /// <summary>The revision selector resolves to no revision.</summary>
  public const string RevisionNotFound = "RevisionNotFound";

  /// <summary>The create input violates the pinned markdown subset.</summary>
  public const string MarkdownInvalid = "MarkdownInvalid";

  /// <summary>Non-PNG/JPEG media, or a listed name that is not attached.</summary>
  public const string ImageNotAttachable = "ImageNotAttachable";

  /// <summary>The path does not name an existing file.</summary>
  public const string FileNotFound = "FileNotFound";
}
