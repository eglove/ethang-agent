using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>The docx seam (task 1): the domain knows nothing about OOXML;
///     implementations translate. All paths are pre-resolved workspace paths.
///     Every method stats the file FIRST and returns the content-true
///     <see cref="FileVersion"/> (issue #117 pattern) — the version the read surfaces
///     and the mutations require back. Expected failures flow as typed
///     <see cref="DomainError"/> values carrying only <see cref="DocxErrorCodes"/> codes —
///     never exceptions.</summary>
public interface IDocxAccess
{
  /// <summary>Reads one page of the flat body block list plus document metadata.</summary>
  Task<Result<DocxSnapshot>> ReadAsync(string path, int offset, int maxBlocks, CancellationToken ct = default);

  /// <summary>Applies an atomic edit batch: all operations are validated and resolved
  ///     BEFORE any mutation; the first invalid op refuses the whole batch with
  ///     nothing saved. <paramref name="expectedVersion"/> must match the file's
  ///     current version (StaleDocument otherwise).</summary>
  Task<Result<DocxEditOutcome>> EditAsync(string path, FileVersion expectedVersion, IReadOnlyList<DocxOperation> operations, CancellationToken ct = default);

  /// <summary>Creates a new .docx from the pinned markdown subset (fails MarkdownInvalid
  ///     when the input violates it; fails NotADocx-adjacent FileNotFound only when the
  ///     directory is missing — the file itself must not exist).</summary>
  Task<Result<DocxSaveOutcome>> CreateAsync(string path, string markdown, CancellationToken ct = default);

  /// <summary>Lists the document's comments.</summary>
  Task<Result<IReadOnlyList<DocxCommentInfo>>> ListCommentsAsync(string path, CancellationToken ct = default);

  /// <summary>Adds a comment anchored at the addressed block (version-guarded).</summary>
  Task<Result<DocxSaveOutcome>> AddCommentAsync(string path, FileVersion expectedVersion, string address, string author, string text, CancellationToken ct = default);

  /// <summary>Deletes one comment by id (version-guarded).</summary>
  Task<Result<DocxSaveOutcome>> DeleteCommentAsync(string path, FileVersion expectedVersion, int commentId, CancellationToken ct = default);

  /// <summary>Lists the document's tracked changes.</summary>
  Task<Result<IReadOnlyList<DocxRevisionInfo>>> ListRevisionsAsync(string path, CancellationToken ct = default);

  /// <summary>Accepts or rejects tracked changes matching the scope (version-guarded).</summary>
  Task<Result<DocxSaveOutcome>> ResolveRevisionsAsync(string path, FileVersion expectedVersion, bool accept, DocxRevisionScope scope, CancellationToken ct = default);

  /// <summary>Extracts embedded media as model-attachable images. Names filter the set
  ///     (null = all); non-PNG/JPEG media and listed-but-absent names fail
  ///     ImageNotAttachable.</summary>
  Task<Result<IReadOnlyList<ToolResultImage>>> ExtractImagesAsync(string path, IReadOnlyList<string>? names, CancellationToken ct = default);
}
