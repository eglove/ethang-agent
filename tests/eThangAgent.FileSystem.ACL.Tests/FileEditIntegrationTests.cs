using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class FileEditIntegrationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-e").FullName;
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

  private async Task<string> WriteAsync(string name, string content)
  {
    string p = Path.Combine(_root, name);
    await File.WriteAllTextAsync(p, content).ConfigureAwait(true);
    return p;
  }

  [Fact]
  public async Task SingleOccurrence_Replaced_ReportsNewLineCount()
  {
    string p = await WriteAsync("a.txt", "one\ntwo\nthree");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "two", "TWO", occurrences: 1);
    Assert.True(r.IsSuccess);
    Assert.Equal(1, r.Value!.Replaced);
    Assert.Equal(3, r.Value.NewLineCount);
    Assert.Equal("one\nTWO\nthree", File.ReadAllText(p));
  }

  [Fact]
  public async Task ReplaceAll_NullOccurrences_ReplacesEveryMatch()
  {
    string p = await WriteAsync("b.txt", "x-x-x");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "x", "y", occurrences: null);
    Assert.True(r.IsSuccess);
    Assert.Equal(3, r.Value!.Replaced);
    Assert.Equal("y-y-y", File.ReadAllText(p));
  }

  [Fact]
  public async Task OccurrenceMismatch_RequestedMoreThanExists_Fails()
  {
    string p = await WriteAsync("c.txt", "only-one");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "one", "1", occurrences: 2);
    Assert.False(r.IsSuccess);
    Assert.Equal("OccurrenceMismatch", r.Error!.Code);
    Assert.Contains("1", r.Error.Message, StringComparison.Ordinal); // actual count
  }

  [Fact]
  public async Task AnchorMissing_Fails_AnchorNotFound()
  {
    string p = await WriteAsync("d.txt", "nothing here");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "absent", "z", occurrences: 1);
    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error!.Code);
  }

  [Fact]
  public async Task BinaryFile_Fails_BinaryFile()
  {
    string p = Path.Combine(_root, "bin.dat");
    await File.WriteAllBytesAsync(p, [0x01, 0x00, 0x02, 0x03]);
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "a", "b", occurrences: 1);
    Assert.False(r.IsSuccess);
    Assert.Equal("BinaryFile", r.Error!.Code);
  }

  [Fact]
  public async Task MissingFile_Fails_FileNotFound()
  {
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        Path.Combine(_root, "ghost.txt"), "a", "b", occurrences: 1);
    Assert.False(r.IsSuccess);
    Assert.Equal("FileNotFound", r.Error!.Code);
  }

  [Fact]
  public async Task ReplacementWithNewline_IncrementsLineCount()
  {
    string p = await WriteAsync("e.txt", "a\nb");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "b", "b1\nb2\nb3", occurrences: 1);
    Assert.True(r.IsSuccess);
    Assert.Equal(4, r.Value!.NewLineCount);
  }
}
