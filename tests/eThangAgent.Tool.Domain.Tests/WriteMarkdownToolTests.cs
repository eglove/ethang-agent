using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Mirrors WriteToolTests: strict input validation at the tool boundary,
/// verbatim backend-error surfacing, and the exact output contracts for both modes
/// (return-rendered-markdown vs. write-to-file).</summary>
public class WriteMarkdownToolTests
{
    private const string Root = @"C:\ws";
    private const string Resolved = @"C:\ws\a.md";
    private const string DocJson = """{"blocks":[{"type":"text","text":"Hi"}]}""";

    private static WriteMarkdownTool MakeTool(Result<FileWriteOutcome>? outcome = null) =>
        new(new WorkspacePathResolver(Root), new FakeFileWriteAccess(outcome));

    // ---- input validation ----

    [Fact]
    public async Task MissingDocument_ReturnsError()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120}"""));
        Assert.True(result.IsError);
        Assert.Contains("document", result.Content);
    }

    [Fact]
    public async Task Document_NotAnObject_Rejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"document":[1,2]}"""));
        Assert.True(result.IsError);
        Assert.Contains("object", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"document":{"blocks":[]},"mode":"fancy"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("mode", result.Content);
    }

    [Fact]
    public async Task PathWithoutOverwrite_Rejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"path":"a.md","document":""" + DocJson + "}"));
        Assert.True(result.IsError);
        Assert.Contains("overwrite", result.Content);
    }

    [Fact]
    public async Task OverwriteWithoutPath_Rejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"overwrite":false,"document":""" + DocJson + "}"));
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task MalformedBlockInsideDocument_Rejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"document":{"blocks":[{"type":"marquee"}]}}"""));
        Assert.True(result.IsError);
        Assert.Contains("marquee", result.Content);
    }

    [Fact]
    public async Task PathOutsideWorkspace_ReturnsResolverError()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"path":"..\\evil.md","overwrite":true,"document":""" + DocJson + "}"));
        Assert.True(result.IsError);
        Assert.Contains("PathOutsideWorkspace", result.Content);
    }

    // ---- return mode ----

    [Fact]
    public async Task NoPath_ReturnsRenderedMarkdownVerbatim()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("write_markdown",
            """{"timeoutSeconds":120,"document":{"blocks":[{"type":"header","level":1,"text":"T"},{"type":"text","text":"B"}]}}"""));
        Assert.False(result.IsError);
        Assert.Equal("# T\n\nB\n", result.Content);
    }

    // ---- write mode ----

    [Fact]
    public async Task WithPath_WritesAndFormatsAnnotationLine_Created()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(true, 42)))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":false,"document":""" + DocJson + "}"));
        Assert.False(result.IsError);
        Assert.Equal($"[write_markdown {Resolved}] created, 42 bytes", result.Content);
    }

    [Fact]
    public async Task WithPath_Overwritten_FormatsAnnotationLine()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(false, 7)))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":true,"document":""" + DocJson + "}"));
        Assert.False(result.IsError);
        Assert.Equal($"[write_markdown {Resolved}] overwritten, 7 bytes", result.Content);
    }

    [Fact]
    public async Task FileExists_SurfacesBackendError()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Failure(
                new Error("FileExists", "File already exists: a.md")))
            .ExecuteAsync(new RawToolInput("write_markdown",
                """{"timeoutSeconds":120,"path":"a.md","overwrite":false,"document":""" + DocJson + "}"));
        Assert.True(result.IsError);
        Assert.Contains("Error [FileExists]", result.Content);
    }

    // ---- advertisement contract ----

    [Fact]
    public void OnlyTimeoutSecondsAndDocument_AreRequired()
    {
        var tool = new WriteMarkdownTool(new UnrootedPathResolver(), new FakeFileWriteAccess(null));
        Assert.Equal(["timeoutSeconds", "document"], tool.Definition.RequiredParameters);
    }

    [Fact]
    public void Description_StatesBothModesAndOverwriteGate()
    {
        var tool = new WriteMarkdownTool(new UnrootedPathResolver(), new FakeFileWriteAccess(null));
        Assert.Contains("verbatim", tool.Definition.Description);
        Assert.Contains("Optional", Param(tool, "path").Description);
        Assert.Contains("required when 'path' is present", Param(tool, "overwrite").Description);
    }

    private static ToolParameter Param(WriteMarkdownTool tool, string name) =>
        Assert.Single(tool.Definition.Parameters, p => p.Name == name);

    private sealed class FakeFileWriteAccess(Result<FileWriteOutcome>? outcome) : IFileWriteAccess
    {
        public Task<Result<FileWriteOutcome>> WriteFileAsync(
            string path, string content, bool overwrite, CancellationToken ct = default)
            => Task.FromResult(outcome ?? Result<FileWriteOutcome>.Failure(new Error("Unused", "not exercised")));
    }
}
