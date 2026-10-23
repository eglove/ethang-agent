using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileEditAccess
{
  /// <summary>Literal (non-regex) replacement. When <paramref name="occurrences"/>
  ///     is null every occurrence is replaced; otherwise the actual count must equal it.
  ///     The replacement's line endings are normalized to the file's dominant convention
  ///     before splicing, so an edit never introduces a foreign terminator; untouched
  ///     regions keep their exact bytes. Refuses binary files. Never creates files.</summary>
  Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
      string path, string oldText, string newText, int? occurrences, CancellationToken ct = default);

  /// <summary>Replaces the line range <paramref name="startLine"/>..<paramref name="endLine"/>
  ///     (1-based, inclusive, counted exactly as ReadLinesAsync counts) with
  ///     <paramref name="newText"/>, whose line endings are normalized to the file's
  ///     dominant convention. An empty <paramref name="newText"/> deletes the range.
  ///     'endLine' past EOF is rejected, never clamped. Refuses binary files. Never creates files.</summary>
  Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
      string path, int startLine, int endLine, string newText, CancellationToken ct = default);
}
