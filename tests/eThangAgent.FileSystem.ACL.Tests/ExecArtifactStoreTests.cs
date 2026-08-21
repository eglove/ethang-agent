using eThangAgent.FileSystem.ACL;

namespace eThangAgent.FileSystem.ACL.Tests;

public class ExecArtifactStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"ethang-artifacts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteAsync_CreatesDirectory_WritesContent_ReturnsPath()
    {
        var store = new ExecArtifactStore(_dir);

        var path = await store.WriteAsync("full output text");

        Assert.StartsWith(_dir, path);
        Assert.True(File.Exists(path));
        Assert.Equal("full output text", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_Twice_ReturnsDistinctPaths()
    {
        var store = new ExecArtifactStore(_dir);

        var first = await store.WriteAsync("one");
        var second = await store.WriteAsync("two");

        Assert.NotEqual(first, second);
    }
}
