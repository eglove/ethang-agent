using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>T1 GREEN will make these pass: the ACL captures the file's
///     (Length, LastWriteTimeUtc) AT THE READ MOMENT — stat-first, before reading
///     lines — so the version cannot see a write that landed after the content was
///     read. The version rides FileRead.</summary>
public sealed class DirectFileSystemAccessVersionTests : IDisposable
{
  private readonly string _tempDir;
  public DirectFileSystemAccessVersionTests()
      => _tempDir = Path.Combine(Path.GetTempPath(), "ethang-dfs-version-" + Guid.NewGuid().ToString("N"));

  public void Dispose()
  {
    try
    {
      Directory.Delete(_tempDir, true);
    }
    catch (Exception)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task ReadLinesAsync_CarriesVersionMatchingFilePair()
  {
    string path = Path.Combine(_tempDir, "f.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "one\ntwo\nthree", TestContext.Current.CancellationToken);

    DirectFileSystemAccess access = new();
    Result<FileRead> r = await access.ReadLinesAsync(path, 1, 3, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    FileInfo stat = new(path);
    FileVersion? version = r.Value.Version;
    Assert.NotNull(version);
    Assert.Equal(stat.Length, version.Length);
    Assert.Equal(stat.LastWriteTimeUtc, version.LastWriteTimeUtc);
  }

  [Fact]
  public async Task ReadLinesAsync_VersionChangesAfterModify()
  {
    string path = Path.Combine(_tempDir, "f.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "one\ntwo", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<FileRead> first = await access.ReadLinesAsync(path, 1, 2, ct: TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(path, "one\ntwo\nthree", TestContext.Current.CancellationToken);
    Result<FileRead> second = await access.ReadLinesAsync(path, 1, 3, ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    Assert.NotNull(first.Value.Version);
    Assert.NotNull(second.Value.Version);
    Assert.NotEqual(first.Value.Version, second.Value.Version);
    Assert.NotEqual(first.Value.Version.Token, second.Value.Version.Token);
  }

  [Fact]
  public async Task ReadLinesAsync_SameLengthRewrite_ChangesVersion()
  {
    // In-place same-length rewrite: the exact failure mode Length alone misses.
    string path = Path.Combine(_tempDir, "f.txt");
    _ = Directory.CreateDirectory(_tempDir);
    await File.WriteAllTextAsync(path, "aaaa\nbbbb", TestContext.Current.CancellationToken);
    DirectFileSystemAccess access = new();

    Result<FileRead> first = await access.ReadLinesAsync(path, 1, 2, ct: TestContext.Current.CancellationToken);
    // Cross a clock tick: two same-length writes inside one timer tick can share a
    // timestamp (the named residual risk; the hash upgrade path covers it). Real
    // re-reads are seconds apart.
    await Task.Delay(50, TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(path, "cccc\ndddd", TestContext.Current.CancellationToken);
    Result<FileRead> second = await access.ReadLinesAsync(path, 1, 2, ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    FileVersion? firstVersion = first.Value.Version;
    FileVersion? secondVersion = second.Value.Version;
    Assert.NotNull(firstVersion);
    Assert.NotNull(secondVersion);
    Assert.NotEqual(firstVersion, secondVersion);
  }

  [Fact]
  public async Task ReadLinesAsync_FileNotFound_FailureCarriesNoVersion()
  {
    DirectFileSystemAccess access = new();
    Result<FileRead> r = await access.ReadLinesAsync(Path.Combine(_tempDir, "nope.txt"), 1, 5, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("FileNotFound", r.Error.Code);
  }
}
