using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class DirectFileSystemAccess : IFileSystemAccess, IDisposable
{
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return Task.FromResult(Result<FileRead>.Failure(new Error("FileNotFound", $"File not found: {path}")));

        var allLines = new List<string>();
        // Count total lines by reading everything once (acceptable for tool-requested bounded reads)
        using var sr = new StreamReader(path, Encoding.UTF8);
        while (sr.ReadLine() is { } line)
            allLines.Add(line);

        var start = Math.Max(1, startLine) - 1;
        var end = Math.Min(endLine, allLines.Count);
        var slice = allLines.Skip(start).Take(end - start).ToList();
        return Task.FromResult(Result<FileRead>.Success(new FileRead(slice, end, allLines.Count)));
    }

    public void Dispose() { }
}
