using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileSystemAccess
{
  Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default);

  /// <summary>Reads a whole file as raw bytes (documents, media — anything not line-oriented
  ///     text). Callers enforce their own size bounds BEFORE calling; this reads all of it.</summary>
  Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default);
}
