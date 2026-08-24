using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class EditToolTests
{
    private const string Root = @"C:\ws";
    private const string Resolved = @"C:\ws\a.txt";

    private static EditTool MakeTool(Result<ReplaceOutcome> outcome) =>
        new(new WorkspacePathResolver(Root), new FakeFileEditAccess(outcome));

    private const string Args = """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":1}""";

    // ---- Missing parameters ----

    [Fact]
    public async Task MissingPath_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"old":"x","new":"y","all":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task MissingOld_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","new":"y","all":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("old", result.Content);
    }

    [Fact]
    public async Task MissingNew_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","all":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("new", result.Content);
    }

    // ---- Selector rules ----

    [Fact]
    public async Task NeitherAllNorOccurrences_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y"}"""));
        Assert.True(result.IsError);
        Assert.Contains("exactly one", result.Content);
    }

    [Fact]
    public async Task BothAllAndOccurrences_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":true,"occurrences":2}"""));
        Assert.True(result.IsError);
        Assert.Contains("exactly one", result.Content);
    }

    [Fact]
    public async Task AllFalse_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("exactly one", result.Content);
    }

    [Fact]
    public async Task OccurrencesZero_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":0}"""));
        Assert.True(result.IsError);
        Assert.Contains("occurrences", result.Content);
        Assert.Contains("\u2265 1", result.Content);
    }

    // ---- Types & unknown ----

    [Fact]
    public async Task OccurrencesAsString_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","occurrences":"1"}"""));
        Assert.True(result.IsError);
        Assert.Contains("occurrences", result.Content);
        Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OldEmpty_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"","new":"y","all":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("old", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            "{\"timeoutSeconds\":120,\"path\":\"a.txt\",\"old\":\"x\",\"new\":\"y\",\"all\":true,\"regex\":true}"));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("regex", result.Content);
    }

    // ---- Path jail ----

    [Fact]
    public async Task PathOutsideWorkspace_ReturnsResolverError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"..\\evil.txt","old":"x","new":"y","all":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("PathOutsideWorkspace", result.Content);
    }

    // ---- Success formatting ----

    [Fact]
    public async Task ReplaceAll_FormatsAnnotationLine_Plural()
    {
        var result = await MakeTool(Result<ReplaceOutcome>.Success(new(3, 5)))
            .ExecuteAsync(new RawToolInput("edit",
            """{"timeoutSeconds":120,"path":"a.txt","old":"x","new":"y","all":true}"""));
        Assert.False(result.IsError);
        Assert.Equal($"[edit {Resolved}] replaced 3 occurrence(s), file now 5 lines", result.Content);
    }

    [Fact]
    public async Task SingleOccurrence_SingularWording()
    {
        var result = await MakeTool(Result<ReplaceOutcome>.Success(new(1, 2)))
            .ExecuteAsync(new RawToolInput("edit", Args));
        Assert.False(result.IsError);
        Assert.Equal($"[edit {Resolved}] replaced 1 occurrence, file now 2 lines", result.Content);
    }

    // ---- Backend errors surface verbatim ----

    [Theory]
    [InlineData("AnchorNotFound", "Anchor text not found")]
    [InlineData("OccurrenceMismatch", "occurs 2 time(s)")]
    public async Task BackendErrors_SurfaceVerbatim(string code, string message)
    {
        var result = await MakeTool(Result<ReplaceOutcome>.Failure(new Error(code, message)))
            .ExecuteAsync(new RawToolInput("edit", Args));
        Assert.True(result.IsError);
        Assert.Contains($"Error [{code}]", result.Content);
        Assert.Contains(message, result.Content);
    }

    private sealed class FakeFileEditAccess(Result<ReplaceOutcome> outcome) : IFileEditAccess
    {
        public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
            string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
            => Task.FromResult(outcome);
    }
}
