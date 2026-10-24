using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Multi-hunk edits (issue #252): one atomic pass over ONE file read, all or
///     nothing. A hunk that matches nothing applies NOTHING — the file is byte-identical
///     afterwards — and the failure names the hunk and why.</summary>
public sealed class HunkEditIntegrationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-hunks").FullName;
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

  private static EditHunk Counted(string oldText, string replacement, int occurrences) =>
      new(oldText, replacement, false, occurrences);

  private static EditHunk All(string oldText, string replacement) =>
      new(oldText, replacement, true, 0);

  [Fact]
  public async Task TwoDisjointHunks_AreBothApplied_InOneWrite()
  {
    string p = await WriteAsync("a.txt", "one\ntwo\nthree\nfour");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(
        p, [Counted("two", "TWO", 1), Counted("four", "FOUR", 1)],
        ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal([1, 1], r.Value.ReplacedPerHunk);
    Assert.Equal(4, r.Value.NewLineCount);
    Assert.Equal("one\nTWO\nthree\nFOUR", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task HunkReplacementShiftsLaterLines_LaterHunksStillMatch()
  {
    // The classic multi-hunk hazard: the first hunk grows the file, invalidating any
    // line number a model was holding. Anchor matching is content-addressed, so the
    // later hunk still lands on the same text.
    string p = await WriteAsync("b.txt", "header\nbody\nfooter");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("body", "line one\nline two\nline three", 1), Counted("footer", "FOOTER", 1)],
        ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal("header\nline one\nline two\nline three\nFOOTER",
        await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task OneHunkFails_AppliesNothing_AndTheFileIsUntouched()
  {
    const string Original = "one\ntwo\nthree";
    string p = await WriteAsync("c.txt", Original);
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("one", "1", 1), Counted("absent", "2", 1), Counted("three", "3", 1)],
        ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.Contains("hunk 2", r.Error.Message, StringComparison.Ordinal);
    Assert.Equal(Original, await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task OccurrenceMismatchInsideAHunk_FailsTheWholeCall_NamingTheHunk()
  {
    string p = await WriteAsync("d.txt", "x y x");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("x", "z", 1)], ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("OccurrenceMismatch", r.Error.Code);
    Assert.Contains("hunk 1", r.Error.Message, StringComparison.Ordinal);
    Assert.Equal("x y x", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task HunkAllSelector_ReplacesEveryOccurrence()
  {
    string p = await WriteAsync("e.txt", "x y x z x");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [All("x", "q")], ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal([3], r.Value.ReplacedPerHunk);
    Assert.Equal("q y q z q", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task IdenticalAnchorInTwoHunks_IsRefused_AsOverlapping()
  {
    // Two hunks naming the same anchor is a contradictory request, not a sequence:
    // applying the second to the first's output is a different edit than either alone.
    string p = await WriteAsync("f.txt", "one two three");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("two", "TWO", 1), Counted("two", "DEUX", 1)],
        ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("HunksOverlap", r.Error.Code);
    Assert.Contains("hunk 2", r.Error.Message, StringComparison.Ordinal);
    Assert.Equal("one two three", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task EmptyReplacement_DeletesTheMatchedText()
  {
    string p = await WriteAsync("g.txt", "keep this\ndrop me\nkeep that");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("\ndrop me", "", 1)], ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal("keep this\nkeep that", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task CRLFFile_UntouchedBytesKeepCRLF_AndReplacementsAreNormalized()
  {
    string p = await WriteAsync("h.txt", "one\r\ntwo\r\nthree");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("two", "TWO\nTWO-B", 1)], ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal(LineEnding.CrLf, r.Value.NormalizedTo);
    Assert.Equal("one\r\nTWO\r\nTWO-B\r\nthree",
        await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task AnchorWithLF_MatchesCRLFContent()
  {
    string p = await WriteAsync("i.txt", "alpha\r\nbeta\r\ngamma");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("alpha\nbeta", "A\nB", 1)], ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsSuccess, r.IsSuccess ? "" : r.Error.Message);
    Assert.Equal("A\r\nB\r\ngamma", await File.ReadAllTextAsync(p, TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task BinaryFile_Fails_BinaryFile()
  {
    string p = Path.Combine(_root, "bin.dat");
    await File.WriteAllBytesAsync(p, [0x01, 0x00, 0x02, 0x03], TestContext.Current.CancellationToken);
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("a", "b", 1)], ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("BinaryFile", r.Error.Code);
  }

  [Fact]
  public async Task MissingFile_Fails_FileNotFound_AndIsNeverCreated()
  {
    string p = Path.Combine(_root, "ghost.txt");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("a", "b", 1)], ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("FileNotFound", r.Error.Code);
    Assert.False(File.Exists(p));
  }

  [Fact]
  public async Task MissWithoutANearMatch_StillNamesTheHunk()
  {
    string p = await WriteAsync("j.txt", "alpha beta gamma");
    Result<HunkReplaceOutcome> r = await _access.ReplaceHunksAsync(p,
        [Counted("completely unrelated", "x", 1)], ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("AnchorNotFound", r.Error.Code);
    Assert.Contains("hunk 1", r.Error.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("Nearest match", r.Error.Message, StringComparison.Ordinal);
  }
}
