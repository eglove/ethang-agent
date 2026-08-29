using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class EditToolTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.txt";

  private static EditTool MakeTool(Result<ReplaceOutcome> outcome) =>
      new(new WorkspacePathResolver(Root), new FakeFileEditAccess(outcome));

  private const string Args = /*lang=json,strict*/ """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":1}""";

  // ---- Missing parameters ----

  [Fact]
  public async Task MissingPath_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"old":"x","new":"y","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingOld_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","new":"y","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("old", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingNew_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("new", result.Content, StringComparison.Ordinal);
  }

  // ---- Selector rules ----

  [Fact]
  public async Task NeitherAllNorOccurrences_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("exactly one", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BothAllAndOccurrences_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":true,"occurrences":2}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("exactly one", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AllFalse_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":false}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("exactly one", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task OccurrencesZero_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":0}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("occurrences", result.Content, StringComparison.Ordinal);
    Assert.Contains("\u2265 1", result.Content, StringComparison.Ordinal);
  }

  // ---- Types & unknown ----

  [Fact]
  public async Task OccurrencesAsString_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":"1"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("occurrences", result.Content, StringComparison.Ordinal);
    Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task OldEmpty_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"","new":"y","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("old", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"old\":\"x\",\"new\":\"y\",\"all\":true,\"regex\":true}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("regex", result.Content, StringComparison.Ordinal);
  }

  // ---- Path jail ----

  [Fact]
  public async Task PathOutsideWorkspace_ReturnsResolverError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"..\\evil.txt","old":"x","new":"y","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  // ---- Success formatting ----

  [Fact]
  public async Task ReplaceAll_FormatsAnnotationLine_Plural()
  {
    ToolResult result = await MakeTool(Result.Success<ReplaceOutcome>(new(3, 5)))
            .ExecuteAsync(new RawToolInput("edit",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":true}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[edit {Resolved}] replaced 3 occurrence(s), file now 5 lines", result.Content);
  }

  [Fact]
  public async Task SingleOccurrence_SingularWording()
  {
    ToolResult result = await MakeTool(Result.Success<ReplaceOutcome>(new(1, 2)))
            .ExecuteAsync(new RawToolInput("edit", Args), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[edit {Resolved}] replaced 1 occurrence, file now 2 lines", result.Content);
  }

  // ---- Backend errors surface verbatim ----

  [Theory]
  [InlineData("AnchorNotFound", "Anchor text not found")]
  [InlineData("OccurrenceMismatch", "occurs 2 time(s)")]
  public async Task BackendErrors_SurfaceVerbatim(string code, string message)
  {
    ToolResult result = await MakeTool(Result.Failure<ReplaceOutcome>(new DomainError(code, message)))
            .ExecuteAsync(new RawToolInput("edit", Args), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains($"Error [{code}]", result.Content, StringComparison.Ordinal);
    Assert.Contains(message, result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeFileEditAccess(Result<ReplaceOutcome> outcome) : IFileEditAccess
  {
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
        => Task.FromResult(outcome);
  }
}
