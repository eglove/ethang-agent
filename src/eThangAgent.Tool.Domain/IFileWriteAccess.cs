using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileWriteAccess
{
  /// <summary>Creates or replaces a file as UTF-8 without BOM. The text's line endings
  ///     are normalized to the file's dominant convention (a new file takes the
  ///     project's), so a caller never has to reason about EOLs. Never creates
  ///     parent directories. Never overwrites unless <paramref name="overwrite"/> is true.</summary>
  Task<Result<FileWriteOutcome>> WriteFileAsync(
      string path, string content, bool overwrite, CancellationToken ct = default);

  /// <summary>Creates or replaces a file from one string per line, joined with the
  ///     resolved line-ending convention (the file's own when it exists, else the
  ///     project's). Same rules as <see cref="WriteFileAsync"/>: no directory creation,
  ///     no silent overwrites.</summary>
  Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
      string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default);

  /// <summary>Creates or replaces a file with raw bytes (images and other binary
  ///     artifacts). Same rules as <see cref="WriteFileAsync"/>: no directory creation,
  ///     no silent overwrites.</summary>
  Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
      string path, byte[] bytes, bool overwrite, CancellationToken ct = default);
}
