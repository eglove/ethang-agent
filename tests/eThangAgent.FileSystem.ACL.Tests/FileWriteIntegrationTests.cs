using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class FileWriteIntegrationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-w").FullName;
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

  [Fact]
  public async Task WriteNewFile_Succeeds_CreatedTrue()
  {
    string path = Path.Combine(_root, "new.txt");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "hello", overwrite: false);
    Assert.True(r.IsSuccess);
    Assert.True(r.Value.Created);
    Assert.Equal("hello", await File.ReadAllTextAsync(path));
  }

  [Fact]
  public async Task WriteExisting_WithoutOverwrite_Fails_FileExists()
  {
    string path = Path.Combine(_root, "x.txt");
    _ = await _access.WriteFileAsync(path, "first", overwrite: false);
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "second", overwrite: false);
    Assert.False(r.IsSuccess);
    Assert.Equal("FileExists", r.Error.Code);
    Assert.Equal("first", await File.ReadAllTextAsync(path)); // unchanged
  }

  [Fact]
  public async Task WriteExisting_WithOverwrite_ReplacesContent_CreatedFalse()
  {
    string path = Path.Combine(_root, "y.txt");
    _ = await _access.WriteFileAsync(path, "old", overwrite: false);
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "brand new content", overwrite: true);
    Assert.True(r.IsSuccess);
    Assert.False(r.Value.Created);
    Assert.Equal("brand new content", await File.ReadAllTextAsync(path));
  }

  [Fact]
  public async Task BytesWritten_ReflectsUtf8ByteCount_NoBom()
  {
    string path = Path.Combine(_root, "bytes.txt");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "\u00e9", overwrite: false); // \u00e9 = 2 UTF-8 bytes
    Assert.True(r.IsSuccess);
    Assert.Equal(2L, r.Value.BytesWritten);
    byte[] raw = await File.ReadAllBytesAsync(path);
    Assert.NotEqual(0xEF, raw[0]); // no BOM
  }

  [Fact]
  public async Task MissingParentDirectory_Fails_DirectoryNotFound()
  {
    string path = Path.Combine(_root, "no", "such", "dir", "f.txt");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "x", overwrite: false);
    Assert.False(r.IsSuccess);
    Assert.Equal("DirectoryNotFound", r.Error.Code);
  }
}
