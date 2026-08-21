using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class ExecArtifactStore : IExecOutputStore
{
    private readonly string _directory;

    public ExecArtifactStore(string? directory = null)
        => _directory = directory ?? ExecOptions.Default.ArtifactDirectory;

    public async Task<string> WriteAsync(string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory,
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }
}
