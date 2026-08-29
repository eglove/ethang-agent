using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class DirectFileSystemAccessTests : IDisposable
{
  private readonly string _tempDir;
  public DirectFileSystemAccessTests() => _tempDir = Path.Combine(Path.GetTempPath(), "ethang-dfs-tests-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    try
    {
      Directory.Delete(_tempDir, true);
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

  [Fact]
  public async Task ReadLinesAsync_ReadsInRange()
  {
    string path = Path.Combine(_tempDir, "test.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "line1\nline2\nline3\nline4\nline5", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<FileRead> r = await access.ReadLinesAsync(path, 2, 4, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(["line2", "line3", "line4"], r.Value.Lines);
    Assert.Equal(4, r.Value.LastLineRead);
    Assert.Equal(5, r.Value.TotalLines);
  }

  [Fact]
  public async Task ReadLinesAsync_FileNotFound_ReturnsError()
  {
    DirectFileSystemAccess access = new();
    Result<FileRead> r = await access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("FileNotFound", r.Error.Code);
  }

  [Fact]
  public async Task WriteFileAsync_CreatesFile()
  {
    string path = Path.Combine(_tempDir, "new.txt");
    _ = Directory.CreateDirectory(_tempDir);
    DirectFileSystemAccess access = new();

    Result<FileWriteOutcome> r = await access.WriteFileAsync(path, "hello world", overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.True(r.Value.Created);
    Assert.Equal(11L, r.Value.BytesWritten);
    Assert.Equal("hello world", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteFileAsync_FileExistsWithoutOverwrite_ReturnsError()
  {
    string path = Path.Combine(_tempDir, "exists.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<FileWriteOutcome> r = await access.WriteFileAsync(path, "new", overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("FileExists", r.Error.Code);
  }

  [Fact]
  public async Task ReplaceInFileAsync_AllOccurrences()
  {
    string path = Path.Combine(_tempDir, "replace.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "a X b X c", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<ReplaceOutcome> r = await access.ReplaceInFileAsync(path, "X", "Y", occurrences: null, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.Value.Replaced);
    Assert.Equal("a Y b Y c", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task ReplaceInFileAsync_AnchorNotFound_ReturnsError()
  {
    string path = Path.Combine(_tempDir, "noanchor.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<ReplaceOutcome> r = await access.ReplaceInFileAsync(path, "XYZ", "Z", occurrences: null, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
  }

  [Fact]
  public async Task SearchFilesAsync_LiteralMatch_FindsLines()
  {
    string path = Path.Combine(_tempDir, "search.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<FileSearch> r = await access.SearchFilesAsync(_tempDir, "beta", regex: false, glob: null, maxResults: 10, contextLines: 0, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    _ = Assert.Single(r.Value.Matches);
    Assert.Equal("beta", r.Value.Matches[0].Lines[0]);
    Assert.Equal(2, r.Value.Matches[0].LineNumber);
  }
}
