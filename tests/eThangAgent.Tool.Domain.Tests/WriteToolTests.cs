using eThangAgent.SharedKernel;

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
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"content":"x","overwrite":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingContent_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","overwrite":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("content", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingOverwrite_ReturnsError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","content":"x"}"""));
    Assert.True(result.IsError);
    Assert.Contains("overwrite", result.Content, StringComparison.Ordinal);
  }

  // ---- Wrong types ----

  [Fact]
  public async Task Overwrite_MustBeBoolean_StringRejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":"yes"}"""));
    Assert.True(result.IsError);
    Assert.Contains("overwrite", result.Content, StringComparison.Ordinal);
    Assert.Contains("boolean", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Content_MustBeString_NumberRejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","content":42,"overwrite":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("content", result.Content, StringComparison.Ordinal);
    Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":true,"encoding":"utf16"}"""));
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("encoding", result.Content, StringComparison.Ordinal);
  }

  // ---- Path jail ----

  [Fact]
  public async Task PathOutsideWorkspace_ReturnsResolverError()
  {
    ToolResult result = await MakeTool(null!).ExecuteAsync(new RawToolInput("write",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"..\\evil.txt","content":"x","overwrite":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  // ---- Success formatting ----

  [Fact]
  public async Task Created_FormatsAnnotationLine()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(true, 42)))
            .ExecuteAsync(new RawToolInput("write",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":false}"""));
    Assert.False(result.IsError);
    Assert.Equal($"[write {Resolved}] created, 42 bytes", result.Content);
  }

  [Fact]
  public async Task Overwritten_FormatsAnnotationLine()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(false, 7)))
            .ExecuteAsync(new RawToolInput("write",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":true}"""));
    Assert.False(result.IsError);
    Assert.Equal($"[write {Resolved}] overwritten, 7 bytes", result.Content);
  }

  [Fact]
  public async Task EmptyContent_Allowed_ZeroBytes()
  {
    ToolResult result = await MakeTool(Result.Success<FileWriteOutcome>(new(true, 0)))
            .ExecuteAsync(new RawToolInput("write",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"path":"empty.txt","content":"","overwrite":false}"""));
    Assert.False(result.IsError);
    Assert.Contains("created, 0 bytes", result.Content, StringComparison.Ordinal);
  }

  // ---- Backend errors surface verbatim ----

  [Fact]
  public async Task FileExists_SurfacesBackendError()
  {
    ToolResult result = await MakeTool(Result.Failure<FileWriteOutcome>(
                new DomainError("FileExists", "File already exists: a.txt")))
            .ExecuteAsync(new RawToolInput("write",
                                     /*lang=json,strict*/
                                     """{"timeoutSeconds":120,"path":"a.txt","content":"x","overwrite":false}"""));
    Assert.True(result.IsError);
    Assert.Contains("Error [FileExists]", result.Content, StringComparison.Ordinal);
  }

  private sealed class FakeFileWriteAccess(Result<FileWriteOutcome> outcome) : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
        => Task.FromResult(outcome);
  }
}
