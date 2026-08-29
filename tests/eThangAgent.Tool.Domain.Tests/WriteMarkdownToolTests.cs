using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Mirrors WriteToolTests: strict input validation at the tool boundary,
/// verbatim backend-error surfacing, and the exact output contracts for both modes
/// (return-rendered-markdown vs. write-to-file).</summary>
public class WriteMarkdownToolTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.md";
  private const string DocJson = /*lang=json,strict*/ """{"blocks":[{"type":"text","text":"Hi"}]}""";

  private static WriteMarkdownTool MakeTool(Result<FileWriteOutcome>? outcome = null) =>
      new(new WorkspacePathResolver(Root), new FakeFileWriteAccess(outcome));

  // ---- input validation ----

  [Fact]
  public async Task MissingDocument_ReturnsError()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("document", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Document_NotAnObject_Rejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"document":[1,2]}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("object", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"document":{"blocks":[]},"mode":"fancy"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("mode", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PathWithoutOverwrite_DefaultsToRefuse()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(true, 42))).ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"path":"a.md","document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[write_markdown {Resolved}] created, 42 bytes", result.Content);
  }

  [Fact]
  public async Task OverwriteWithoutPath_Rejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"overwrite":false,"document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedBlockInsideDocument_Rejected()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"document":{"blocks":[{"type":"marquee"}]}}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("marquee", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PathOutsideWorkspace_ReturnsResolverError()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"path":"..\\evil.md","overwrite":true,"document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  // ---- return mode ----

  [Fact]
  public async Task NoPath_ReturnsRenderedMarkdownVerbatim()
  {
    ToolResult result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"document":{"blocks":[{"type":"header","level":1,"text":"T"},{"type":"text","text":"B"}]}}"""), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("# T\n\nB\n", result.Content);
  }

  // ---- write mode ----

  [Fact]
  public async Task WithPath_WritesAndFormatsAnnotationLine_Created()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(true, 42)))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":false,"document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[write_markdown {Resolved}] created, 42 bytes", result.Content);
  }

  [Fact]
  public async Task WithPath_Overwritten_FormatsAnnotationLine()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(false, 7)))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":true,"document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[write_markdown {Resolved}] overwritten, 7 bytes", result.Content);
  }

  [Fact]
  public async Task FileExists_SurfacesBackendError()
  {
    ToolResult result = await MakeTool(Result.Failure<FileWriteOutcome>(
                new DomainError("FileExists", "File already exists: a.md")))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":false,"document":""" + DocJson + "}"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [FileExists]", result.Content, StringComparison.Ordinal);
  }

  // ---- advertisement contract ----

  [Fact]
  public void OnlyTimeoutSecondsAndDocument_AreRequired()
  {
    WriteMarkdownTool tool = new(new UnrootedPathResolver(), new FakeFileWriteAccess(null));
    Assert.Equal(["timeoutSeconds", "document"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Description_StatesBothModesAndOverwriteGate()
  {
    WriteMarkdownTool tool = new(new UnrootedPathResolver(), new FakeFileWriteAccess(null));
    Assert.Contains("verbatim", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("Optional", Param(tool, "path").Description, StringComparison.Ordinal);
    Assert.Contains("Defaults to refusing", Param(tool, "overwrite").Description, StringComparison.Ordinal);
  }

  private static ToolParameter Param(WriteMarkdownTool tool, string name) =>
      Assert.Single(tool.Definition.Parameters, p => p.Name == name);

  private sealed class FakeFileWriteAccess(Result<FileWriteOutcome>? outcome) : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
        => Task.FromResult(outcome ?? Result.Failure<FileWriteOutcome>(new DomainError("Unused", "not exercised")));
  }
}
