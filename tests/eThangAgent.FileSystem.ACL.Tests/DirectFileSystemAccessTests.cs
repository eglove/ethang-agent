using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class DirectFileSystemAccessTests : IDisposable
{
    private readonly string _tempDir;
    public DirectFileSystemAccessTests() => _tempDir = Path.Combine(Path.GetTempPath(), "ethang-dfs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public async Task ReadLinesAsync_ReadsInRange()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "line1\nline2\nline3\nline4\nline5");
        var access = new DirectFileSystemAccess();

        var r = await access.ReadLinesAsync(path, 2, 4);

        Assert.True(r.IsSuccess);
        Assert.Equal(new[] { "line2", "line3", "line4" }, r.Value!.Lines);
        Assert.Equal(4, r.Value.LastLineRead);
        Assert.Equal(5, r.Value.TotalLines);
    }

    [Fact]
    public async Task ReadLinesAsync_FileNotFound_ReturnsError()
    {
        var access = new DirectFileSystemAccess();
        var r = await access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5);

        Assert.False(r.IsSuccess);
        Assert.Equal("FileNotFound", r.Error!.Code);
    }
}
