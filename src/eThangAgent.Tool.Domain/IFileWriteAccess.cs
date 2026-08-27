using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileWriteAccess
{
  /// <summary>Creates or replaces a file as UTF-8 without BOM. Never creates
  ///     parent directories. Never overwrites unless <paramref name="overwrite"/> is true.</summary>
  Task<Result<FileWriteOutcome>> WriteFileAsync(
      string path, string content, bool overwrite, CancellationToken ct = default);

  /// <summary>Creates or replaces a file with raw bytes (images and other binary
  ///     artifacts). Same rules as <see cref="WriteFileAsync"/>: no directory creation,
  ///     no silent overwrites.</summary>
  Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
      string path, byte[] bytes, bool overwrite, CancellationToken ct = default);
}
