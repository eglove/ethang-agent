using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Anchor-miss diagnostics: when an edit anchor matches nothing, the failure
///     names the file's nearest matching region — exact line numbers and exact
///     content in the read tool's gutter format — so one retry corrects the anchor
///     instead of a re-read. The hint appears only when the file contains something
///     similar; a file with no near match fails exactly as before.
///     Issue #252 makes the retry CHEAP rather than merely possible: the miss also
///     emits a ready-to-use 'old' (the exact text that WOULD match at that location),
///     so the correction is a paste, not a re-read. Issue #259 rules that the hint
///     never reports `similarity 100%` for an anchor that did not match — it states
///     the mismatch kind instead, because the figure is computed over trimmed lines.</summary>
public sealed class AnchorHintTests : IDisposable
{
  /// <summary>Marks the emitted ready-to-use anchor inside the miss message.</summary>
  private const string ReadyAnchorMarker = "ready-to-use 'old'";

  private readonly string _root = Directory.CreateTempSubdirectory("ethang-anchor").FullName;
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

  /// <summary>The ready-to-use anchor is everything after the marker line; the block
  ///     is the miss message's tail, so re-sending it must succeed on its own.</summary>
  private static string ReadyAnchorOf(string message)
  {
    string[] lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    int marker = Array.FindIndex(lines, l => l.Contains(ReadyAnchorMarker, StringComparison.Ordinal));
    Assert.True(marker >= 0, "the miss message carried no ready-to-use anchor:\n" + message);
    return string.Join("\n", lines.Skip(marker + 1));
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
    Assert.DoesNotContain(ReadyAnchorMarker, r.Error.Message, StringComparison.Ordinal);
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
    Assert.DoesNotContain(ReadyAnchorMarker, r.Error.Message, StringComparison.Ordinal);
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

  // ---- Issue #252: the emitted anchor is directly usable ----

  [Fact]
  public async Task AnchorMiss_EmitsAReadyAnchor_ThatSucceedsOnTheVeryNextCall()
  {
    string p = await WriteAsync("ready1.cs", "var a = 1;\nvar b = 2\nvar c = 3;");
    Result<ReplaceOutcome> miss = await _access.ReplaceInFileAsync(
        p, "var b = 2;", "var b = 2;", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.False(miss.IsSuccess);

    string ready = ReadyAnchorOf(miss.Error.Message);
    Assert.Equal("var b = 2", ready);

    // The retry is one call with no re-read: the emitted anchor is the anchor.
    Result<ReplaceOutcome> retry = await _access.ReplaceInFileAsync(
        p, ready, "var b = 2;", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.True(retry.IsSuccess, retry.IsSuccess ? "" : retry.Error.Message);
    Assert.Equal("var a = 1;\nvar b = 2;\nvar c = 3;", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task AnchorMiss_MultiLineAnchor_EmitsTheWholeMatchingRegionsExactLines()
  {
    // Wrong indentation on every line of a 3-line anchor: the ready anchor is the
    // file's own three lines, verbatim.
    string p = await WriteAsync("ready2.cs", "class C\n{\n    void M()\n    {\n        Run();\n    }\n}");
    Result<ReplaceOutcome> miss = await _access.ReplaceInFileAsync(
        p, "void M()\n      {\n          Run();", "X", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.False(miss.IsSuccess);

    string ready = ReadyAnchorOf(miss.Error.Message);
    Assert.Equal("    void M()\n    {\n        Run();", ready);

    Result<ReplaceOutcome> retry = await _access.ReplaceInFileAsync(
        p, ready, "    void M()", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.True(retry.IsSuccess, retry.IsSuccess ? "" : retry.Error.Message);
  }

  [Fact]
  public async Task AnchorMiss_TrailingWhitespaceInAnchor_EmitsTheFilesExactLine()
  {
    string p = await WriteAsync("ready3.txt", "alpha\nbeta gamma\ndelta");
    Result<ReplaceOutcome> miss = await _access.ReplaceInFileAsync(
        p, "beta  gamma", "x", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.False(miss.IsSuccess);

    Assert.Equal("beta gamma", ReadyAnchorOf(miss.Error.Message));
  }

  // ---- Issue #259: the similarity figure never contradicts the failure ----

  [Fact]
  public async Task AnchorMiss_WhitespaceOnlyDifference_StatesTheMismatchKind_NotOneHundredPercent()
  {
    string p = await WriteAsync("ws.cs", "class C\n{\n    void M()\n    {\n        Run();\n    }\n}");
    Result<ReplaceOutcome> miss = await _access.ReplaceInFileAsync(
        p, "void  M()\n    {\n        Run();", "X", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.False(miss.IsSuccess);

    Assert.DoesNotContain("100%", miss.Error.Message, StringComparison.Ordinal);
    Assert.Contains("differs from this region only in whitespace", miss.Error.Message, StringComparison.Ordinal);
    Assert.Contains("similarity not stated", miss.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnchorMiss_RealContentDifference_KeepsTheSimilarityFigure()
  {
    string p = await WriteAsync("sim.txt", "alpha\nbeta gamma delta\nepsilon");
    Result<ReplaceOutcome> miss = await _access.ReplaceInFileAsync(
        p, "beta gamma epsilon", "x", occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.False(miss.IsSuccess);

    Assert.Contains("similarity", miss.Error.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("only in whitespace", miss.Error.Message, StringComparison.Ordinal);
  }
}
