using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL;

public sealed class ExecArtifactStore(string? directory = null) : IExecOutputStore
{
  private readonly string _directory = directory ?? ExecOptions.Default.ArtifactDirectory;

  public async Task<string> WriteAsync(string content, CancellationToken ct = default)
  {
    _ = Directory.CreateDirectory(_directory);
    string path = Path.Combine(_directory,
        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
    return path;
  }
}
