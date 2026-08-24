using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class WriteToolTests
{
    private const string Root = @"C:\ws";
    private const string Resolved = @"C:\ws\a.txt";

    private static WriteTool MakeTool(Result<FileWriteOutcome> outcome) =>
        new(new WorkspacePathResolver(Root), new FakeFileWriteAccess(outcome));

    // ---- Missing parameters ----

    [Fact]
    public async Task MissingPath_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"content":"x","overwrite":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task MissingContent_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"a.txt","overwrite":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("content", result.Content);
    }

    [Fact]
    public async Task MissingOverwrite_ReturnsError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"a.txt","content":"x"}"""));
        Assert.True(result.IsError);
        Assert.Contains("overwrite", result.Content);
    }

    // ---- Wrong types ----

    [Fact]
    public async Task Overwrite_MustBeBoolean_StringRejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":"yes"}"""));
        Assert.True(result.IsError);
        Assert.Contains("overwrite", result.Content);
        Assert.Contains("boolean", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Content_MustBeString_NumberRejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"a.txt","content":42,"overwrite":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("content", result.Content);
        Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":true,"encoding":"utf16"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("encoding", result.Content);
    }

    // ---- Path jail ----

    [Fact]
    public async Task PathOutsideWorkspace_ReturnsResolverError()
    {
        var result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
            """{"timeoutSeconds":120,"path":"..\\evil.txt","content":"x","overwrite":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("PathOutsideWorkspace", result.Content);
    }

    // ---- Success formatting ----

    [Fact]
    public async Task Created_FormatsAnnotationLine()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(true, 42)))
            .ExecuteAsync(new RawToolInput("write",
                """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":false}"""));
        Assert.False(result.IsError);
        Assert.Equal($"[write {Resolved}] created, 42 bytes", result.Content);
    }

    [Fact]
    public async Task Overwritten_FormatsAnnotationLine()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(false, 7)))
            .ExecuteAsync(new RawToolInput("write",
                """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":true}"""));
        Assert.False(result.IsError);
        Assert.Equal($"[write {Resolved}] overwritten, 7 bytes", result.Content);
    }

    [Fact]
    public async Task EmptyContent_Allowed_ZeroBytes()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Success(new(true, 0)))
            .ExecuteAsync(new RawToolInput("write",
                """{"timeoutSeconds":120,"path":"empty.txt","content":"","overwrite":false}"""));
        Assert.False(result.IsError);
        Assert.Contains("created, 0 bytes", result.Content);
    }

    // ---- Backend errors surface verbatim ----

    [Fact]
    public async Task FileExists_SurfacesBackendError()
    {
        var result = await MakeTool(Result<FileWriteOutcome>.Failure(
                new Error("FileExists", "File already exists: a.txt")))
            .ExecuteAsync(new RawToolInput("write",
                """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":false}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [FileExists]", result.Content);
    }

    private sealed class FakeFileWriteAccess(Result<FileWriteOutcome> outcome) : IFileWriteAccess
    {
        public Task<Result<FileWriteOutcome>> WriteFileAsync(
            string path, string content, bool overwrite, CancellationToken ct = default)
            => Task.FromResult(outcome);
    }
}
