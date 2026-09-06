using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

// Range mode of the edit tool: replace lines N..M with new text (no anchor).
// Mode selection is by argument shape and strictly validated: range mode needs
// path, startLine, endLine, replacement; anchor parameters are rejected alongside it.
public class EditToolRangeModeTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.txt";

  private static EditTool MakeTool(Result<ReplaceOutcome> outcome) =>
      new(new WorkspacePathResolver(Root), new FakeFileEditAccess(outcome));

  [Fact]
  public async Task RangeMode_ReachesSeam_WithResolvedPathAndRange()
  {
    FakeFileEditAccess fake = new(Result.Success<ReplaceOutcome>(new(3, 9)));
    ToolResult result = await new EditTool(new WorkspacePathResolver(Root), fake)
            .ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":2,\"endLine\":4,\"replacement\":\"y\"}"),
                ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(2, fake.LastStartLine);
    Assert.Equal(4, fake.LastEndLine);
    Assert.Equal("y", fake.LastNewText);
    Assert.Equal($"[edit {Resolved}] replaced lines 2-4, file now 9 lines", result.Content);
  }

  [Fact]
  public async Task RangeMode_EmptyReplacement_DeletesLines()
  {
    FakeFileEditAccess fake = new(Result.Success<ReplaceOutcome>(new(2, 5)));
    ToolResult result = await new EditTool(new WorkspacePathResolver(Root), fake)
            .ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":3,\"endLine\":4,\"replacement\":\"\"}"),
                ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("", fake.LastNewText);
    Assert.Equal($"[edit {Resolved}] replaced lines 3-4, file now 5 lines", result.Content);
  }

  // ---- Missing parameters ----

  [Fact]
  public async Task RangeMode_MissingStartLine_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"endLine\":4,\"replacement\":\"y\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RangeMode_MissingEndLine_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":2,\"replacement\":\"y\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("endLine", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RangeMode_MissingReplacement_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":2,\"endLine\":4}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("replacement", result.Content, StringComparison.Ordinal);
  }

  // ---- Range validation ----

  [Fact]
  public async Task RangeMode_StartLineZero_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":0,\"endLine\":4,\"replacement\":\"y\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("\u2265 1", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RangeMode_EndLineBeforeStart_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":5,\"endLine\":2,\"replacement\":\"y\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("endLine", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RangeMode_StartLineAsString_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":\"2\",\"endLine\":4,\"replacement\":\"y\"}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("integer", result.Content, StringComparison.Ordinal);
  }

  // ---- Mode exclusivity ----

  [Fact]
  public async Task MixedMode_OldWithRange_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"old\":\"x\",\"replacement\":\"y\",\"startLine\":2,\"endLine\":4}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("range", result.Content, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("anchor", result.Content, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("old", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MixedMode_AllWithRange_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"replacement\":\"y\",\"all\":true,\"startLine\":2,\"endLine\":4}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("range", result.Content, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("anchor", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task MixedMode_OccurrencesWithRange_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"replacement\":\"y\",\"occurrences\":1,\"startLine\":2,\"endLine\":4}"),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("range", result.Content, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("anchor", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  // ---- Backend errors surface verbatim; advertisement ----

  [Fact]
  public async Task RangeMode_BackendErrorSurfacesVerbatim()
  {
    ToolResult result = await MakeTool(Result.Failure<ReplaceOutcome>(
                new DomainError("LineRangeBeyondEof", "'endLine' 99 exceeds file length (5 lines).")))
            .ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"startLine\":2,\"endLine\":4,\"replacement\":\"y\"}"),
                ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [LineRangeBeyondEof]", result.Content, StringComparison.Ordinal);
    Assert.Contains("exceeds file length", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void RangeMode_IsAdvertised_InDescriptionAndParameters()
  {
    ToolDefinition definition = MakeTool(null!).Definition;
    Assert.Contains("startLine", definition.Description, StringComparison.Ordinal);
    Assert.Contains("endLine", definition.Description, StringComparison.Ordinal);
    Assert.Contains(definition.Parameters, p => p.Name == "startLine");
    Assert.Contains(definition.Parameters, p => p.Name == "endLine");
  }

  private sealed class FakeFileEditAccess(Result<ReplaceOutcome> outcome) : IFileEditAccess
  {
    public string? LastNewText { get; private set; }
    public int LastStartLine { get; private set; }
    public int LastEndLine { get; private set; }

    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
    {
      LastNewText = newText;
      return Task.FromResult(outcome);
    }

    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
    {
      LastStartLine = startLine;
      LastEndLine = endLine;
      LastNewText = newText;
      return Task.FromResult(outcome);
    }
  }
}

