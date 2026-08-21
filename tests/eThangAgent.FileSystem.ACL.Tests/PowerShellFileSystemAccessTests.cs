using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

public class PowerShellFileSystemAccessTests : IDisposable
{
    private readonly PowerShellFileSystemAccess _access = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ethang-fs-{Guid.NewGuid():N}");

    public PowerShellFileSystemAccessTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task MiddleRange_ReturnsRequestedLines()
    {
        var path = WriteFile("test.txt", "a", "b", "c", "d", "e");

        var result = await _access.ReadLinesAsync(path, 2, 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(["b", "c", "d"], result.Value!.Lines);
        Assert.Equal(4, result.Value.LastLineRead);
        Assert.Equal(5, result.Value.TotalLines);
    }

    [Fact]
    public async Task ExactEof_ReturnsAllRequested_NoClampNeeded()
    {
        var path = WriteFile("test.txt", "a", "b", "c");

        var result = await _access.ReadLinesAsync(path, 1, 3);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b", "c"], result.Value!.Lines);
        Assert.Equal(3, result.Value.LastLineRead);
        Assert.Equal(3, result.Value.TotalLines);
    }

    [Fact]
    public async Task EndPastEof_ReturnsAllLines_TotalLinesKnown()
    {
        var path = WriteFile("test.txt", "a", "b");

        var result = await _access.ReadLinesAsync(path, 1, 100);

        Assert.True(result.IsSuccess);
        Assert.Equal(["a", "b"], result.Value!.Lines);
        Assert.Equal(2, result.Value.LastLineRead);
        Assert.Equal(2, result.Value.TotalLines);
    }

    [Fact]
    public async Task StartBeyondEof_LastLineIsZero()
    {
        var path = WriteFile("test.txt", "a", "b");

        var result = await _access.ReadLinesAsync(path, 10, 15);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Lines);
        Assert.Equal(0, result.Value.LastLineRead);
        Assert.Equal(2, result.Value.TotalLines);
    }

    [Fact]
    public async Task MissingFile_ReturnsFileNotFoundError()
    {
        var result = await _access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("FileNotFound", result.Error!.Code);
    }

    [Fact]
    public async Task DirectoryPath_ReturnsFileSystemError()
    {
        var result = await _access.ReadLinesAsync(_tempDir, 1, 5);

        Assert.False(result.IsSuccess);
        Assert.Equal("FileSystemError", result.Error!.Code);
    }

    [Fact]
    public async Task EmptyFile_ReturnsZeroLineZeroTotal()
    {
        var path = WriteFile("empty.txt");

        var result = await _access.ReadLinesAsync(path, 1, 10);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Lines);
        Assert.Equal(0, result.Value.LastLineRead);
        Assert.Equal(0, result.Value.TotalLines);
    }

    [Fact]
    public async Task CRLF_IsNormalized()
    {
        var path = Path.Combine(_tempDir, "crlf.txt");
        File.WriteAllText(path, "line1\r\nline2\r\n", Encoding.UTF8);

        var result = await _access.ReadLinesAsync(path, 1, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(["line1", "line2"], result.Value!.Lines);
        Assert.False(result.Value.Lines.Any(l => l.Contains('\r')));
    }

    [Fact]
    public async Task Utf8Bom_ContentReadCorrectly()
    {
        var path = Path.Combine(_tempDir, "bom.txt");
        File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await _access.ReadLinesAsync(path, 1, 1);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value!.Lines.Single());
    }

    [Fact]
    public async Task RunspaceReuse_TwoSequentialReadsBothSucceed()
    {
        var path1 = WriteFile("a.txt", "aa");
        var path2 = WriteFile("b.txt", "bb");

        var r1 = await _access.ReadLinesAsync(path1, 1, 1);
        var r2 = await _access.ReadLinesAsync(path2, 1, 1);

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);
        Assert.Equal("aa", r1.Value!.Lines.Single());
        Assert.Equal("bb", r2.Value!.Lines.Single());
    }

    [Fact]
    public async Task LargeFile_IsFast()
    {
        var path = Path.Combine(_tempDir, "large.txt");
        var lines = Enumerable.Range(1, 50_000).Select(i => $"line {i}");
        File.WriteAllLines(path, lines, Encoding.UTF8);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _access.ReadLinesAsync(path, 40_001, 40_100);
        watch.Stop();

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value!.Lines.Count);
        Assert.True(watch.ElapsedMilliseconds < 10_000, $"took {watch.ElapsedMilliseconds}ms");
    }
}
