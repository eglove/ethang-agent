using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Anchor-miss diagnostics: when an edit anchor matches nothing, the failure
///     names the file's nearest matching region — exact line numbers and exact
///     content in the read tool's gutter format — so one retry corrects the anchor
///     instead of a re-read. The hint appears only when the file contains something
///     similar; a file with no near match fails exactly as before.</summary>
public sealed class AnchorHintTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-anchor").FullName;
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
  public async Task AnchorMiss_NearMatch_NamesLineAndExactContent()
  {
    // The anchor misses by one character (missing semicolon): the hint must
    // point at line 2 and quote its exact content.
    string p = await WriteAsync("a.cs", "var a = 1;\nvar b = 2\nvar c = 3;");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        p, "var b = 2;", "var b = 2;", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.Contains("Nearest match at line 2", r.Error.Message, StringComparison.Ordinal);
    Assert.Contains("2→ var b = 2", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnchorMiss_IndentationDifference_NamesTheExactLine()
  {
    // The anchor uses the wrong indentation depth on a multi-line block: its first
    // line matches a file line after trimming, and the hint quotes that region.
    string p = await WriteAsync("b.cs", "class C\n{\n    void M()\n    {\n        Run();\n    }\n}");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        p, "void M()\n        {\n            Run();\n        }", "void M()\n    {\n        Run();\n    }", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.Contains("Nearest match at line 3", r.Error.Message, StringComparison.Ordinal);
    Assert.Contains("3→     void M()", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnchorMiss_NoNearMatch_FailsWithoutHint()
  {
    string p = await WriteAsync("c.txt", "alpha beta gamma");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        p, "completely unrelated text", "x", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.DoesNotContain("Nearest match", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnchorMiss_EmptyFile_FailsWithoutHint()
  {
    string p = await WriteAsync("d.txt", "");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        p, "anything", "x", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.DoesNotContain("Nearest match", r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnchorMiss_HintCarriesTheOriginalMessage()
  {
    string p = await WriteAsync("e.txt", "one\ntwo\nthree");
    Result<ReplaceOutcome> r = await _access.ReplaceInFileAsync(
        p, "absent", "z", occurrences: 1, ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Contains("not found in", r.Error.Message, StringComparison.Ordinal);
  }
}
