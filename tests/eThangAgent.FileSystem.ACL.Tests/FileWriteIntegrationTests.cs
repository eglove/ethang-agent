using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class FileWriteIntegrationTests : IDisposable
{
  private readonly string _root = TestTempRoot.CreateDirectory("ethang-w").FullName;
  private readonly DirectFileSystemAccess _access = new(TextConventionStub.For(LineEnding.Lf));

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
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "hello", overwrite: false, ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.True(r.Value.Created);
    Assert.Equal("hello", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteExisting_WithoutOverwrite_Fails_FileExists()
  {
    string path = Path.Combine(_root, "x.txt");
    _ = await _access.WriteFileAsync(path, "first", overwrite: false, ct: TestContext.Current.CancellationToken);
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "second", overwrite: false, ct: TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("FileExists", r.Error.Code);
    Assert.Equal("first", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)); // unchanged
  }

  [Fact]
  public async Task WriteExisting_WithOverwrite_ReplacesContent_CreatedFalse()
  {
    string path = Path.Combine(_root, "y.txt");
    _ = await _access.WriteFileAsync(path, "old", overwrite: false, ct: TestContext.Current.CancellationToken);
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "brand new content", overwrite: true, ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.False(r.Value.Created);
    Assert.Equal("brand new content", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteExisting_DrasticShrink_IsRefused_ContentShrunk_AndTheFileIsUntouched()
  {
    // A round-tripped read result that lost its body overwrote a 151-line report with 38
    // lines. That shape is a truncation far more often than an intended rewrite, so it is
    // refused rather than performed.
    string path = Path.Combine(_root, "report.txt");
    string full = string.Join("\n", Enumerable.Repeat("a line of the report", 40));
    _ = await _access.WriteFileAsync(path, full, overwrite: false, ct: TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "a line of the report", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("ContentShrunk", r.Error.Code);
    Assert.Equal(full, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteExisting_ModestShrink_IsAllowed()
  {
    string path = Path.Combine(_root, "notes.txt");
    string full = string.Join("\n", Enumerable.Repeat("a line of notes", 20));
    _ = await _access.WriteFileAsync(path, full, overwrite: false, ct: TestContext.Current.CancellationToken);

    // Two thirds of the file survives: an ordinary edit, not a lost body.
    string trimmed = string.Join("\n", Enumerable.Repeat("a line of notes", 13));
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, trimmed, overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(trimmed, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteExisting_ShortFileShrinkingToNothing_IsAllowed()
  {
    // Below the floor there is no body to lose: a short file being rewritten short is
    // ordinary editing.
    string path = Path.Combine(_root, "small.txt");
    _ = await _access.WriteFileAsync(path, "a short note", overwrite: false, ct: TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "x", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("x", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task BytesWritten_ReflectsUtf8ByteCount_NoBom()
  {
    string path = Path.Combine(_root, "bytes.txt");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "\u00e9", overwrite: false, ct: TestContext.Current.CancellationToken); // \u00e9 = 2 UTF-8 bytes
    Assert.True(r.IsSuccess);
    Assert.Equal(2L, r.Value.BytesWritten);
    byte[] raw = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
    Assert.NotEqual(0xEF, raw[0]); // no BOM
  }

  [Fact]
  public async Task MissingParentDirectory_Fails_DirectoryNotFound_NamingTheMissingDirectory()
  {
    string missing = Path.Combine(_root, "no", "such", "dir");
    string path = Path.Combine(missing, "f.txt");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(path, "x", overwrite: false, ct: TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("DirectoryNotFound", r.Error.Code);
    Assert.Contains(missing, r.Error.Message, StringComparison.Ordinal);
    Assert.Contains("Create it first", r.Error.Message, StringComparison.Ordinal);
  }
}
