using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileSearchIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-s").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<string> WriteAsync(string relative, string content)
    {
        var p = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, content);
        return p;
    }

    [Fact]
    public async Task LiteralMatch_ReportsPathLineAndText()
    {
        await WriteAsync("src\\a.cs", "alpha\nbeta\ngamma");
        var r = await _access.SearchFilesAsync(_root, "beta", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("a.cs", m.Path);
        Assert.Equal(2, m.LineNumber);
        Assert.Equal("beta", m.Lines[0].Trim());
    }

    [Fact]
    public async Task ContextLines_IncludesNeighbors()
    {
        await WriteAsync("b.txt", "one\ntwo\nthree\nfour");
        var r = await _access.SearchFilesAsync(_root, "two", regex: false, glob: null, maxResults: 50, contextLines: 1);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.Equal(3, m.Lines.Count);
        Assert.Equal("one", m.Lines[0].Trim());
        Assert.Equal("three", m.Lines[2].Trim());
    }

    [Fact]
    public async Task RegexMode_MatchesPattern()
    {
        await WriteAsync("c.txt", "foo123\nbar\nfoo456");
        var r = await _access.SearchFilesAsync(_root, "foo\\d+", regex: true, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Equal(2, r.Value!.Matches.Count);
    }

    [Fact]
    public async Task InvalidRegex_Fails_InvalidPattern()
    {
        var r = await _access.SearchFilesAsync(_root, "foo(", regex: true, glob: null, maxResults: 50, contextLines: 0);
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidPattern", r.Error!.Code);
    }

    [Fact]
    public async Task GitDirectory_Skipped()
    {
        await WriteAsync(".git\\tracked.txt", "secret-token");
        await WriteAsync("real.txt", "secret-token");
        var r = await _access.SearchFilesAsync(_root, "secret-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("real.txt", m.Path);
    }

    [Fact]
    public async Task BinaryFiles_Skipped()
    {
        await WriteAsync("bin.dat", "x\0y"); // NUL byte
        var r = await _access.SearchFilesAsync(_root, "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Matches);
    }

    [Fact]
    public async Task GlobFilter_RestrictsFiles()
    {
        await WriteAsync("keep.cs", "needle");
        await WriteAsync("skip.md", "needle");
        var r = await _access.SearchFilesAsync(_root, "needle", regex: false, glob: "*.cs", maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        var m = Assert.Single(r.Value!.Matches);
        Assert.EndsWith("keep.cs", m.Path);
    }

    [Fact]
    public async Task MaxResults_TruncatesWithFlag()
    {
        for (var i = 0; i < 5; i++)
            await WriteAsync($"f{i}.txt", "hit");
        var r = await _access.SearchFilesAsync(_root, "hit", regex: false, glob: null, maxResults: 3, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Equal(3, r.Value!.Matches.Count);
        Assert.True(r.Value.Truncated);
    }

    [Fact]
    public async Task NoMatches_ReportsFilesScanned()
    {
        await WriteAsync("z.txt", "nothing relevant");
        var r = await _access.SearchFilesAsync(_root, "absent-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Matches);
        Assert.False(r.Value.Truncated);
        Assert.Equal(1, r.Value.FilesScanned);
    }

    [Fact]
    public async Task MissingRoot_Fails_RootNotFound()
    {
        var r = await _access.SearchFilesAsync(Path.Combine(_root, "ghost"), "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
        Assert.False(r.IsSuccess);
        Assert.Equal("RootNotFound", r.Error!.Code);
    }
}
