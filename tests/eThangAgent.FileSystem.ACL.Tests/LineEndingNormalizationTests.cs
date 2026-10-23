using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>The issue-#251 contract at the WRITE boundary: incoming text lands in the
/// convention the file (or failing that, the project) already uses, and untouched
/// regions keep their exact bytes. These drive the real ACL over a real temp tree.
/// <para>Each write reports the convention it landed (<c>NormalizedTo</c>) so the tool
/// layer can state it in its annotation line instead of leaving the model to guess.</para></summary>
public sealed class LineEndingNormalizationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-eoln").FullName;
  private readonly DirectFileSystemAccess _lf = new(LineEndingStub.For(LineEnding.Lf));
  private readonly DirectFileSystemAccess _crlf = new(LineEndingStub.For(LineEnding.CrLf));

  public void Dispose()
  {
    _lf.Dispose();
    _crlf.Dispose();
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

  private string At(string name) => Path.Combine(_root, name);

  // ---- write ----

  [Fact]
  public async Task Write_CrLfTextOverLfFile_LandsLf()
  {
    string path = At("a.txt");
    await File.WriteAllTextAsync(path, "one\ntwo\nthree\n", TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _lf.WriteFileAsync(
        path, "alpha\r\nbeta\r\n", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    string after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
    Assert.Equal("alpha\nbeta\n", after);
    Assert.DoesNotContain('\r', after);
  }

  [Fact]
  public async Task Write_LfTextOverCrLfFile_LandsCrLf()
  {
    string path = At("b.txt");
    await File.WriteAllTextAsync(path, "one\r\ntwo\r\n", TestContext.Current.CancellationToken);

    // The FILE's dominant convention beats the project policy: an existing CRLF file
    // stays CRLF even under an LF project.
    Result<FileWriteOutcome> r = await _lf.WriteFileAsync(
        path, "alpha\nbeta\n", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("alpha\r\nbeta\r\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Write_NewFile_UsesResolvedProjectConvention()
  {
    string path = At("new.txt");

    Result<FileWriteOutcome> r = await _crlf.WriteFileAsync(
        path, "alpha\nbeta\n", overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("alpha\r\nbeta\r\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Write_ReportsTheConventionItLanded()
  {
    string path = At("c.txt");
    await File.WriteAllTextAsync(path, "one\r\ntwo", TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _lf.WriteFileAsync(
        path, "x\ny", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(LineEnding.CrLf, r.Value.NormalizedTo);
  }

  [Fact]
  public async Task Write_TextAlreadyInConvention_StillReportsIt()
  {
    string path = At("d.txt");

    Result<FileWriteOutcome> r = await _lf.WriteFileAsync(
        path, "x\ny", overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(LineEnding.Lf, r.Value.NormalizedTo);
    Assert.Equal("x\ny", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Write_ByteCount_ReflectsTheNormalizedLength()
  {
    string path = At("e.txt");
    await File.WriteAllTextAsync(path, "one\r\ntwo\r\n", TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _lf.WriteFileAsync(
        path, "x\ny\n", overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("x\r\ny\r\n".Length, r.Value.BytesWritten);
  }

  // ---- edit: anchor mode ----

  [Fact]
  public async Task Edit_AnchorMode_ReplacementNormalizedToFileConvention()
  {
    string path = At("f.txt");
    await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\n", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _lf.ReplaceInFileAsync(
        path, "beta", "BETA1\r\nBETA2", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    string after = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
    Assert.Equal("alpha\nBETA1\nBETA2\ngamma\n", after);
    Assert.DoesNotContain('\r', after);
  }

  [Fact]
  public async Task Edit_AnchorMode_OntoCrLfFile_ReplacementBecomesCrLf()
  {
    string path = At("g.txt");
    await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _lf.ReplaceInFileAsync(
        path, "beta", "BETA1\nBETA2", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("alpha\r\nBETA1\r\nBETA2\r\ngamma", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Edit_AnchorMode_TolerantMatchStillWorks()
  {
    // H2: normalization must not disturb the anchor-matching offset map.
    string path = At("h.txt");
    await File.WriteAllTextAsync(path, "alpha\r\nbeta\r\ngamma", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _lf.ReplaceInFileAsync(
        path, "alpha\nbeta", "ALPHA", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("ALPHA\r\ngamma", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  // ---- edit: range mode ----

  [Fact]
  public async Task Edit_RangeMode_ReplacementNormalizedToFileConvention()
  {
    string path = At("i.txt");
    await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _lf.ReplaceLineRangeAsync(
        path, 2, 3, "TWO\r\nTHREE", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("one\nTWO\nTHREE\nfour", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Edit_RangeMode_OntoCrLfFile_ReplacementBecomesCrLf()
  {
    string path = At("j.txt");
    await File.WriteAllTextAsync(path, "one\r\ntwo\r\nthree\r\nfour", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _lf.ReplaceLineRangeAsync(
        path, 2, 3, "TWO\nTHREE", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("one\r\nTWO\r\nTHREE\r\nfour", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Edit_RangeMode_ReportsTheFileConvention()
  {
    string path = At("k.txt");
    await File.WriteAllTextAsync(path, "one\ntwo\n", TestContext.Current.CancellationToken);

    Result<ReplaceOutcome> r = await _crlf.ReplaceLineRangeAsync(
        path, 1, 1, "ONE", ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal(LineEnding.Lf, r.Value.NormalizedTo);
  }

  // ---- lines mode ----

  [Fact]
  public async Task WriteLines_JoinsWithResolvedConvention()
  {
    string path = At("l.txt");

    Result<FileWriteOutcome> r = await _crlf.WriteFileLinesAsync(
        path, ["first", "second"], overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("first\r\nsecond", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteLines_ExistingFileKeepsItsOwnConvention()
  {
    string path = At("m.txt");
    await File.WriteAllTextAsync(path, "one\n", TestContext.Current.CancellationToken);

    Result<FileWriteOutcome> r = await _crlf.WriteFileLinesAsync(
        path, ["first", "second"], overwrite: true, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("first\nsecond", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task WriteLines_EmptyArray_WritesEmptyFile()
  {
    string path = At("n.txt");

    Result<FileWriteOutcome> r = await _crlf.WriteFileLinesAsync(
        path, [], overwrite: false, ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess);
    Assert.Equal("", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
  }
}
