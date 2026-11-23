using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Canonical <see cref="DomainError"/> codes for the pptx surface: the only
///     codes an <see cref="IPptxAccess"/> failure may carry. The tool renders them as
///     standard 'Error [Code]: message' blocks; the ACL is the only producer.
///     </summary>
public static class PptxErrorCodes
{
  /// <summary>The file is not a PresentationML package: its first four bytes are not a
  ///     zip signature, or the package carries no ppt/presentation.xml part. A .ppt
  ///     (binary), a .ppsx, a .potx, and a .docx renamed to .pptx all land here.</summary>
  public const string NotAPptx = "NotAPptx";

  /// <summary>The package contains a ppt/vbaProject.bin part, so the deck is
  ///     macro-enabled (.pptm content).</summary>
  public const string MacroPresentation = "MacroPresentation";

  /// <summary>The file is a zip package but cannot be read as a presentation: the
  ///     presentation part is missing, or the package is truncated or otherwise
  ///     unreadable.</summary>
  public const string CorruptPresentation = "CorruptPresentation";

  /// <summary>The file exceeds PptxLimits.MaxFileBytes, measured before any part is
  ///     read.</summary>
  public const string OversizePresentation = "OversizePresentation";

  /// <summary>The expectedVersion the caller supplied does not match the file's current
  ///     version - the file changed under the model since its read. Nothing is written.
  ///     </summary>
  public const string StalePresentation = "StalePresentation";

  /// <summary>The address text violates the pinned pptx address grammar, or a value
  ///     that must be a 1-based index is not one. The message restates the grammar.
  ///     </summary>
  public const string InvalidAddress = "InvalidAddress";

  /// <summary>The address is grammatically valid but resolves to no shape, table cell,
  ///     notes or animation effect on the addressed slide.</summary>
  public const string AddressNotFound = "AddressNotFound";

  /// <summary>The slide number is outside 1..SlideCount for the file's current state.
  ///     </summary>
  public const string SlideNotFound = "SlideNotFound";

  /// <summary>The layout name resolves to no layout in the deck. The message lists the
  ///     deck's available layout names.</summary>
  public const string LayoutNotFound = "LayoutNotFound";

  /// <summary>The operation would leave content with no place on the deck: deleting the
  ///     last remaining slide, or a layout change that would strand a shape. The message
  ///     names what would be stranded.</summary>
  public const string StrandedContent = "StrandedContent";

  /// <summary>The chart part carries no embedded workbook, so its values cannot be read
  ///     or written. Values are never invented.</summary>
  public const string ChartDataMissing = "ChartDataMissing";

  /// <summary>The chart's type is outside the supported families (bar, column, line,
  ///     pie) and its data was asked to change. The message names the actual type.
  ///     </summary>
  public const string ChartTypeUnsupported = "ChartTypeUnsupported";

  /// <summary>The animation effect index is below 1 or more than one past the slide's
  ///     last effect.</summary>
  public const string AnimationNotFound = "AnimationNotFound";

  /// <summary>The comment id resolves to no comment in either the legacy or the current
  ///     comment shape.</summary>
  public const string CommentNotFound = "CommentNotFound";

  /// <summary>The create source violates the pinned markdown subset (an empty source).
  ///     </summary>
  public const string MarkdownInvalid = "MarkdownInvalid";

  /// <summary>Media that is not PNG or JPEG, a listed name that is not attached to the
  ///     deck, or a replacement image whose bytes are not PNG or JPEG.</summary>
  public const string ImageNotAttachable = "ImageNotAttachable";

  /// <summary>The operation cannot be performed faithfully: it is outside the supported
  ///     vocabulary (a motion path, a trigger sequence, a multi-step effect chain), or
  ///     its task has not yet added it. The message states what is supported.</summary>
  public const string OperationUnsupported = "OperationUnsupported";

  /// <summary>The path does not name an existing file, or a create target's parent
  ///     directory does not exist.</summary>
  public const string FileNotFound = "FileNotFound";
}
