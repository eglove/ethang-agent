using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class FileSearchIntegrationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-s").FullName;
  private readonly DirectFileSystemAccess _access = new();

  public void Dispose()
  {
    _access.Dispose();
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  private async Task<string> WriteAsync(string relative, string content)
  {
    string p = Path.Combine(_root, relative);
    _ = Directory.CreateDirectory(Path.GetDirectoryName(p)!);
    await File.WriteAllTextAsync(p, content).ConfigureAwait(true);
    return p;
  }

  [Fact]
  public async Task LiteralMatch_ReportsPathLineAndText()
  {
    _ = await WriteAsync("src\\a.cs", "alpha\nbeta\ngamma");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "beta", regex: false, glob: null, maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    SearchMatch m = Assert.Single(r.Value.Matches);
    Assert.EndsWith("a.cs", m.Path, StringComparison.Ordinal);
    Assert.Equal(2, m.LineNumber);
    Assert.Equal("beta", m.Lines[0].Trim());
  }

  [Fact]
  public async Task ContextLines_IncludesNeighbors()
  {
    _ = await WriteAsync("b.txt", "one\ntwo\nthree\nfour");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "two", regex: false, glob: null, maxResults: 50, contextLines: 1);
    Assert.True(r.IsSuccess);
    SearchMatch m = Assert.Single(r.Value.Matches);
    Assert.Equal(3, m.Lines.Count);
    Assert.Equal("one", m.Lines[0].Trim());
    Assert.Equal("three", m.Lines[2].Trim());
  }

  [Fact]
  public async Task RegexMode_MatchesPattern()
  {
    _ = await WriteAsync("c.txt", "foo123\nbar\nfoo456");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "foo\\d+", regex: true, glob: null, maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Matches.Count);
  }

  [Fact]
  public async Task InvalidRegex_Fails_InvalidPattern()
  {
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "foo(", regex: true, glob: null, maxResults: 50, contextLines: 0);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidPattern", r.Error.Code);
  }

  [Fact]
  public async Task GitDirectory_Skipped()
  {
    _ = await WriteAsync(".git\\tracked.txt", "secret-token");
    _ = await WriteAsync("real.txt", "secret-token");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "secret-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    SearchMatch m = Assert.Single(r.Value.Matches);
    Assert.EndsWith("real.txt", m.Path, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BinaryFiles_Skipped()
  {
    _ = await WriteAsync("bin.dat", "x\0y"); // NUL byte
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value.Matches);
  }

  [Fact]
  public async Task GlobFilter_RestrictsFiles()
  {
    _ = await WriteAsync("keep.cs", "needle");
    _ = await WriteAsync("skip.md", "needle");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "needle", regex: false, glob: "*.cs", maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    SearchMatch m = Assert.Single(r.Value.Matches);
    Assert.EndsWith("keep.cs", m.Path, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MaxResults_TruncatesWithFlag()
  {
    for (int i = 0; i < 5; i++)
    {
      _ = await WriteAsync($"f{i}.txt", "hit");
    }

    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "hit", regex: false, glob: null, maxResults: 3, contextLines: 0);
    Assert.True(r.IsSuccess);
    Assert.Equal(3, r.Value.Matches.Count);
    Assert.True(r.Value.Truncated);
  }

  [Fact]
  public async Task NoMatches_ReportsFilesScanned()
  {
    _ = await WriteAsync("z.txt", "nothing relevant");
    Result<FileSearch> r = await _access.SearchFilesAsync(_root, "absent-token", regex: false, glob: null, maxResults: 50, contextLines: 0);
    Assert.True(r.IsSuccess);
    Assert.Empty(r.Value.Matches);
    Assert.False(r.Value.Truncated);
    Assert.Equal(1, r.Value.FilesScanned);
  }

  [Fact]
  public async Task MissingRoot_Fails_RootNotFound()
  {
    Result<FileSearch> r = await _access.SearchFilesAsync(Path.Combine(_root, "ghost"), "x", regex: false, glob: null, maxResults: 50, contextLines: 0);
    Assert.False(r.IsSuccess);
    Assert.Equal("RootNotFound", r.Error.Code);
  }
}
