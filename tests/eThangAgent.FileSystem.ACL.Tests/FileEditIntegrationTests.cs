using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileEditIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-e").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var p = Path.Combine(_root, name);
        await File.WriteAllTextAsync(p, content);
        return p;
    }

    [Fact]
    public async Task SingleOccurrence_Replaced_ReportsNewLineCount()
    {
        var p = await WriteAsync("a.txt", "one\ntwo\nthree");
        var r = await _access.ReplaceInFileAsync(p, "two", "TWO", occurrences: 1);
        Assert.True(r.IsSuccess);
        Assert.Equal(1, r.Value!.Replaced);
        Assert.Equal(3, r.Value.NewLineCount);
        Assert.Equal("one\nTWO\nthree", File.ReadAllText(p));
    }

    [Fact]
    public async Task ReplaceAll_NullOccurrences_ReplacesEveryMatch()
    {
        var p = await WriteAsync("b.txt", "x-x-x");
        var r = await _access.ReplaceInFileAsync(p, "x", "y", occurrences: null);
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.Replaced);
        Assert.Equal("y-y-y", File.ReadAllText(p));
    }

    [Fact]
    public async Task OccurrenceMismatch_RequestedMoreThanExists_Fails()
    {
        var p = await WriteAsync("c.txt", "only-one");
        var r = await _access.ReplaceInFileAsync(p, "one", "1", occurrences: 2);
        Assert.False(r.IsSuccess);
        Assert.Equal("OccurrenceMismatch", r.Error!.Code);
        Assert.Contains("1", r.Error.Message); // actual count
    }

    [Fact]
    public async Task AnchorMissing_Fails_AnchorNotFound()
    {
        var p = await WriteAsync("d.txt", "nothing here");
        var r = await _access.ReplaceInFileAsync(p, "absent", "z", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("AnchorNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task BinaryFile_Fails_BinaryFile()
    {
        var p = Path.Combine(_root, "bin.dat");
        await File.WriteAllBytesAsync(p, [0x01, 0x00, 0x02, 0x03]);
        var r = await _access.ReplaceInFileAsync(p, "a", "b", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("BinaryFile", r.Error!.Code);
    }

    [Fact]
    public async Task MissingFile_Fails_FileNotFound()
    {
        var r = await _access.ReplaceInFileAsync(
            Path.Combine(_root, "ghost.txt"), "a", "b", occurrences: 1);
        Assert.False(r.IsSuccess);
        Assert.Equal("FileNotFound", r.Error!.Code);
    }

    [Fact]
    public async Task ReplacementWithNewline_IncrementsLineCount()
    {
        var p = await WriteAsync("e.txt", "a\nb");
        var r = await _access.ReplaceInFileAsync(p, "b", "b1\nb2\nb3", occurrences: 1);
        Assert.True(r.IsSuccess);
        Assert.Equal(4, r.Value!.NewLineCount);
    }
}
