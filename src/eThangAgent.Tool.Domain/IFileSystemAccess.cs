using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public interface IFileSystemAccess
{
  Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default);
}
