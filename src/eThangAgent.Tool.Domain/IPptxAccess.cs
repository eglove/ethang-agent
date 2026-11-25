using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The pptx seam: the domain knows nothing about PresentationML;
///     implementations translate. All paths are pre-resolved workspace paths.
///     Every read stats the file FIRST and returns the content-true
///     <see cref="FileVersion"/> - the version the read surfaces and the mutations
///     require back. Expected failures flow as typed <see cref="DomainError"/> values
///     carrying only <see cref="PptxErrorCodes"/> codes - never exceptions.
///     </summary>
public interface IPptxAccess
{
  /// <summary>Reads the deck's outline: every slide in presentation order with its
  ///     title, first line, hidden flag and layout name, plus the deck's embedded media
  ///     and its distinct layout display names.</summary>
  Task<Result<PptxOutline>> ReadOutlineAsync(string path, CancellationToken ct = default);

  /// <summary>Reads one slide: its shapes in document order with their addresses, its
  ///     speaker notes, its media, its comments and its animation effects in playback
  ///     order. A slide number outside 1..SlideCount is SlideNotFound.</summary>
  Task<Result<PptxSlideSnapshot>> ReadSlideAsync(string path, int slide, CancellationToken ct = default);

  /// <summary>Applies an atomic edit batch: all operations are validated and resolved
  ///     BEFORE any mutation; the first invalid op refuses the whole batch with nothing
  ///     saved. <paramref name="expectedVersion"/> must match the file's current version
  ///     (StalePresentation otherwise).</summary>
  Task<Result<PptxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<PptxOperation> operations, CancellationToken ct = default);

  /// <summary>Creates a new .pptx from the pinned markdown subset (fails MarkdownInvalid
  ///     when the input violates it; fails FileNotFound when the parent directory is
  ///     missing - the file itself must not already exist).</summary>
  Task<Result<PptxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default);

  /// <summary>Lists the deck's comments, both the legacy and the current comment shape,
  ///     with the address each one anchors to.</summary>
  Task<Result<IReadOnlyList<PptxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default);

  /// <summary>Adds a comment anchored at the addressed shape, writing the current
  ///     comment shape only (version-guarded).</summary>
  Task<Result<PptxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default);

  /// <summary>Deletes one comment by id from either comment shape (version-guarded).
  ///     </summary>
  Task<Result<PptxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default);

  /// <summary>Extracts the deck's media as model-attachable images. Names filter the
  ///     set (null = all); non-PNG/JPEG media and listed-but-absent names fail
  ///     ImageNotAttachable.</summary>
  Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default);
}
