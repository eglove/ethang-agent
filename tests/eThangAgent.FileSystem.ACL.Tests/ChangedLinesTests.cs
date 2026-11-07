using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Issue #356: edit/write outcomes carry the changed 1-based line numbers the
// advisory diagnostics filter scopes to. The numbers come from the same splitter
// the read path counts with, so they are the numbers the model sees.
public sealed class ChangedLinesTests : IDisposable
{
  private readonly string _root = TestTempRoot.CreateDirectory("ethang-cl").FullName;
  private readonly DirectFileSystemAccess _access = new(LineEndingStub.For(LineEnding.Lf));

  public void Dispose()
  {
    _access.Dispose();
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      Assert.True(true);
    }
    catch (UnauthorizedAccessException)
    {
      Assert.True(true);
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
  public async Task AnchorEdit_ReportsChangedLine()
  {
    string p = await WriteAsync("a.cs", "one\ntwo\nthree");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(p, "two", "TWO", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Equal([2], r.Value.ChangedLines);
  }

  [Fact]
  public async Task RangeEdit_ReportsChangedLines()
  {
    string p = await WriteAsync("a.cs", "one\ntwo\nthree");
    Result<ReplaceOutcome> r = await _access.ReplaceLineRangeAsync(p, 2, 3, "TWO\nTHREE", ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Equal([2, 3], r.Value.ChangedLines);
  }

  [Fact]
  public async Task WriteNewFile_ReportsAllLines()
  {
    string p = Path.Combine(_root, "new.cs");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(p, "one\ntwo", overwrite: false, ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Equal([1, 2], r.Value.ChangedLines);
  }

  [Fact]
  public async Task WriteOverwrite_ReportsOnlyChangedLine()
  {
    string p = await WriteAsync("a.cs", "one\ntwo\nthree");
    Result<FileWriteOutcome> r = await _access.WriteFileAsync(p, "one\nTWO\nthree", overwrite: true, ct: TestContext.Current.CancellationToken);
    Assert.True(r.IsSuccess);
    Assert.Equal([2], r.Value.ChangedLines);
  }
}
