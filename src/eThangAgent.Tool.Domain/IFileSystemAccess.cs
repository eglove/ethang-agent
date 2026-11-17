using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileSystemAccess
{
  /// <summary>Reads a range of lines from a text file. A file whose bytes are not text is
  ///     refused with <c>BinaryFile</c> rather than decoded: a document, an archive, or an
  ///     image read as lines is a page of replacement characters that says nothing about the
  ///     file and everything about the wrong tool having been used.</summary>
  Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default);

  /// <summary>Reads a whole file as raw bytes (documents, media — anything not line-oriented
  ///     text). Callers enforce their own size bounds BEFORE calling; this reads all of it.</summary>
  Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default);
}
