using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

// Multi-hunk mode of the edit tool (issue #252): several {old, replacement, all|occurrences}
// entries applied atomically over ONE file read, with one annotation line per hunk. Any hunk
// failing to match fails the whole call - nothing is written - and the error names the hunk.
// The selector rule is the SAME per hunk as in anchor mode: exactly one of all or occurrences,
// never a silent default (the decision issue #259 records as Option C, rejected).
public class EditToolHunksTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.txt";

  private static EditTool MakeTool(Result<HunkReplaceOutcome> outcome) =>
      new(new WorkspacePathResolver(Root), new FakeHunkEditAccess(outcome));

  private static Task<ToolResult> RunAsync(Result<HunkReplaceOutcome> outcome, string args) =>
      MakeTool(outcome).ExecuteAsync(new RawToolInput("edit", args),
          ct: TestContext.Current.CancellationToken);

  /// <summary>Wraps a hunks array into a complete edit argument object.</summary>
  private static string Args(string hunksJson) =>
      "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"hunks\":" + hunksJson + "}";

  private const string TwoGoodHunks = /*lang=json,strict*/ "[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1},{\"old\":\"p\",\"replacement\":\"q\",\"all\":true}]";
  private const string OneGoodHunk = /*lang=json,strict*/ "[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}]";

  // ---- Shape reaches the seam ----

  [Fact]
  public async Task TwoHunks_ReachTheSeamAsParsedHunks_WithOneAnnotationLineEach()
  {
    FakeHunkEditAccess fake = new(Result.Success(new HunkReplaceOutcome([1, 2], 12, LineEnding.Lf)));
    ToolResult result = await new EditTool(new WorkspacePathResolver(Root), fake)
        .ExecuteAsync(new RawToolInput("edit", Args(TwoGoodHunks)),
            ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(Resolved, fake.LastPath);
    Assert.NotNull(fake.LastHunks);
    Assert.Equal(2, fake.LastHunks.Count);
    Assert.Equal("x", fake.LastHunks[0].Old);
    Assert.Equal("y", fake.LastHunks[0].Replacement);
    Assert.False(fake.LastHunks[0].All);
    Assert.Equal(1, fake.LastHunks[0].Occurrences);
    Assert.Equal("p", fake.LastHunks[1].Old);
    Assert.True(fake.LastHunks[1].All);
    Assert.Equal(string.Join("\n",
        $"[edit {Resolved}] hunk 1/2: replaced 1 occurrence",
        $"[edit {Resolved}] hunk 2/2: replaced 2 occurrence(s)",
        $"[edit {Resolved}] 2 hunks applied, file now 12 lines, LF line endings"),
        result.Content);
  }

  [Fact]
  public async Task OneHunk_UsesSingularWordingInTheSummaryLine()
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 4, LineEnding.CrLf)), Args(OneGoodHunk));

    Assert.False(result.IsError);
    Assert.Equal(string.Join("\n",
        $"[edit {Resolved}] hunk 1/1: replaced 1 occurrence",
        $"[edit {Resolved}] 1 hunk applied, file now 4 lines, CRLF line endings"),
        result.Content);
  }

  // ---- Atomic failure reports WHICH hunk and why ----

  [Fact]
  public async Task SeamFailure_SurfacesVerbatim_NamingTheHunk()
  {
    ToolResult result = await RunAsync(
        Result.Failure<HunkReplaceOutcome>(new DomainError("AnchorNotFound",
            "hunk 2: Anchor text (length 5) not found in C:\\ws\\a.txt.")),
        Args(TwoGoodHunks));

    Assert.True(result.IsError);
    Assert.Contains("Error [AnchorNotFound]", result.Content, StringComparison.Ordinal);
    Assert.Contains("hunk 2", result.Content, StringComparison.Ordinal);
  }

  // ---- Mode exclusivity ----

  [Fact]
  public async Task HunksMixedWithAnchorMode_IsRejected()
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)),
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"old\":\"x\",\"replacement\":\"y\",\"all\":true,\"hunks\":[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}]}");

    Assert.True(result.IsError);
    Assert.Contains("hunks", result.Content, StringComparison.Ordinal);
    Assert.Contains("exactly one mode", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task HunksMixedWithRangeMode_IsRejected()
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)),
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":1,\"endLine\":2,\"replacement\":\"y\",\"hunks\":[]}");

    Assert.True(result.IsError);
    Assert.Contains("exactly one mode", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task HunksMode_TopLevelReplacement_IsRejected()
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)),
        /*lang=json,strict*/
        "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"replacement\":\"z\",\"hunks\":[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}]}");

    Assert.True(result.IsError);
    Assert.Contains("inside each hunk", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task HunksMode_DoesNotRequireTopLevelReplacement()
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)), Args(OneGoodHunk));

    Assert.False(result.IsError);
  }

  // ---- Per-hunk validation, each error naming the hunk ----

  [Fact]
  public async Task EmptyHunks_IsRejected() =>
      await AssertHunkErrorAsync(/*lang=json,strict*/ "[]", "at least one");

  [Fact]
  public async Task HunksNotAnArray_IsRejected() =>
      await AssertHunkErrorAsync(/*lang=json,strict*/ "\"nope\"", "array");

  [Fact]
  public async Task HunkEntryNotAnObject_NamesTheIndex() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}," + "7]",
          "hunks[1]");

  [Fact]
  public async Task HunkMissingOld_NamesTheIndex() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1},{\"replacement\":\"q\",\"occurrences\":1}]",
          "hunks[1]");

  [Fact]
  public async Task HunkMissingReplacement_NamesTheIndex() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"occurrences\":1}]", "hunks[0]");

  [Fact]
  public async Task HunkWithNeitherSelector_IsRejected() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"replacement\":\"q\"}]", "exactly one");

  [Fact]
  public async Task HunkWithBothSelectors_IsRejected() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"replacement\":\"q\",\"all\":true,\"occurrences\":2}]",
          "exactly one");

  [Fact]
  public async Task HunkAllFalse_IsRejected() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"replacement\":\"q\",\"all\":false}]", "exactly true");

  [Fact]
  public async Task HunkOccurrencesZero_IsRejected() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"replacement\":\"q\",\"occurrences\":0}]", "\u2265 1");

  [Fact]
  public async Task HunkOldEmpty_IsRejected() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"\",\"replacement\":\"q\",\"occurrences\":1}]", "empty anchor");

  [Fact]
  public async Task HunkUnknownParameter_NamesTheIndex() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"p\",\"replacement\":\"q\",\"occurrences\":1,\"regex\":true}]",
          "hunks[0]");

  [Fact]
  public async Task HunkSelectorError_NamesTheHunkToo() =>
      await AssertHunkErrorAsync(
          /*lang=json,strict*/ "[{\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1},{\"old\":\"p\",\"replacement\":\"q\"}]",
          "hunks[1]");

  // ---- Advertisement ----

  [Fact]
  public void OnlyTimeoutSecondsAndPath_AreUnconditionallyRequired()
  {
    ToolDefinition definition = MakeTool(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf))).Definition;
    Assert.Equal(["timeoutSeconds", "path"], definition.RequiredParameters);
  }

  [Fact]
  public void Description_DocumentsHunksAtomicityAndPerHunkSelectors()
  {
    string d = MakeTool(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf))).Definition.Description;
    Assert.Contains("HUNKS MODE", d, StringComparison.Ordinal);
    Assert.Contains("atomic", d, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("nothing is applied", d, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("hunk K/N: replaced C occurrence(s)", d, StringComparison.Ordinal);
    Assert.Contains("HunksOverlap", d, StringComparison.Ordinal);
  }

  [Fact]
  public void Hunks_IsAdvertisedAsAnObjectArray()
  {
    ToolDefinition definition = MakeTool(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf))).Definition;
    ToolParameter p = Assert.Single(definition.Parameters, x => x.Name == "hunks");
    Assert.Equal(ToolParameterType.ObjectArray, p.Type);
    Assert.DoesNotContain("hunks", definition.RequiredParameters);
  }

  private static async Task AssertHunkErrorAsync(string hunksJson, string expectedFragment)
  {
    ToolResult result = await RunAsync(
        Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf)), Args(hunksJson)).ConfigureAwait(true);
    Assert.True(result.IsError);
    Assert.Contains(expectedFragment, result.Content, StringComparison.OrdinalIgnoreCase);
  }

  private sealed class FakeHunkEditAccess(Result<HunkReplaceOutcome> outcome) : IFileEditAccess
  {
    public string? LastPath { get; private set; }

    public IReadOnlyList<EditHunk>? LastHunks { get; private set; }

    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default) =>
        throw new InvalidOperationException("anchor mode is not under test here");

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default) =>
        throw new InvalidOperationException("range mode is not under test here");

    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
    {
      LastPath = path;
      LastHunks = hunks;
      return Task.FromResult(outcome);
    }
  }
}
