using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileWriteAccess
{
  /// <summary>Creates or replaces a file as UTF-8 without BOM. Never creates
  /// parent directories. Never overwrites unless <paramref name="overwrite"/> is true.</summary>
  Task<Result<FileWriteOutcome>> WriteFileAsync(
      string path, string content, bool overwrite, CancellationToken ct = default);
}
