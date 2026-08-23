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

    [Fact]
    public async Task WriteFileAsync_CreatesFile()
    {
        var path = Path.Combine(_tempDir, "new.txt");
        Directory.CreateDirectory(_tempDir);
        var access = new DirectFileSystemAccess();

        var r = await ((IFileWriteAccess)access).WriteFileAsync(path, "hello world", overwrite: false);

        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.Created);
        Assert.Equal(11L, r.Value.BytesWritten);
        Assert.Equal("hello world", File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteFileAsync_FileExistsWithoutOverwrite_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "exists.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "old");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileWriteAccess)access).WriteFileAsync(path, "new", overwrite: false);

        Assert.False(r.IsSuccess);
        Assert.Equal("FileExists", r.Error!.Code);
    }

    [Fact]
    public async Task ReplaceInFileAsync_AllOccurrences()
    {
        var path = Path.Combine(_tempDir, "replace.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "a X b X c");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileEditAccess)access).ReplaceInFileAsync(path, "X", "Y", occurrences: null);

        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Value!.Replaced);
        Assert.Equal("a Y b Y c", File.ReadAllText(path));
    }

    [Fact]
    public async Task ReplaceInFileAsync_AnchorNotFound_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "noanchor.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "hello");
        var access = new DirectFileSystemAccess();

        var r = await ((IFileEditAccess)access).ReplaceInFileAsync(path, "XYZ", "Z", occurrences: null);

        Assert.False(r.IsSuccess);
        Assert.Equal("AnchorNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task SearchFilesAsync_LiteralMatch_FindsLines()
    {
        var path = Path.Combine(_tempDir, "search.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "alpha\nbeta\ngamma");
        var access = new DirectFileSystemAccess();

        var r = await ((ISearchAccess)access).SearchFilesAsync(_tempDir, "beta", regex: false, glob: null, maxResults: 10, contextLines: 0);

        Assert.True(r.IsSuccess);
        Assert.Single(r.Value!.Matches);
        Assert.Equal("beta", r.Value.Matches[0].Lines[0]);
        Assert.Equal(2, r.Value.Matches[0].LineNumber);
    }
}
