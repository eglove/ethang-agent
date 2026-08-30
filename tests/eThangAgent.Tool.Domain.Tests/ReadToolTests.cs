using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ReadToolTests
{
  private static ReadTool MakeTool(Result<FileRead> readResult, IPathResolver? resolver = null) =>
      new(resolver ?? new UnrootedPathResolver(), new FakeFileSystemAccess(readResult));

  private static ReadTool MakeTool(FileRead success, IPathResolver? resolver = null) =>
      new(resolver ?? new UnrootedPathResolver(), new FakeFileSystemAccess(Result.Success(success)));

  // ---- JSON parsing ----

  [Fact]
  public async Task RawArguments_NotJson_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read", "not json"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RawArguments_NotObject_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read", "[1,2,3]"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  // ---- Missing parameters ----

  [Fact]
  public async Task MissingPath_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"startLine":1,"endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Missing required", result.Content, StringComparison.Ordinal);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingStartLine_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingEndLine_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":1}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("endLine", result.Content, StringComparison.Ordinal);
  }

  // ---- Wrong types ----

  [Fact]
  public async Task StartLineIsString_ReturnsTypeError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":"1","endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task StartLineIsFloat_ReturnsTypeError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":1.5,"endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task PathIsNumber_ReturnsTypeError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":123,"startLine":1,"endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("path", result.Content, StringComparison.Ordinal);
    Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  // ---- Unknown parameters ----

  [Fact]
  public async Task ExtraParameter_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":5,"encoding":"utf16"}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("encoding", result.Content, StringComparison.Ordinal);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  // ---- Value constraints ----

  [Fact]
  public async Task StartLine_LessThan1_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":0,"endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("≥ 1", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task StartLine_GreaterThanEndLine_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":10,"endLine":5}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("must not exceed", result.Content, StringComparison.Ordinal);
  }

  // ---- Range cap ----

  [Fact]
  public async Task RangeExceeds1000_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2000}"""), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("1000", result.Content, StringComparison.Ordinal);
    Assert.Contains("chunks", result.Content, StringComparison.OrdinalIgnoreCase);
  }

  // ---- Execution (happy path) ----

  [Fact]
  public async Task SuccessfulRead_ReturnsFormattedContent()
  {
    FileRead fileRead = new(["alpha", "beta", "gamma"], 3, 5);
    ReadTool tool = MakeTool(fileRead);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"doc.txt","startLine":1,"endLine":3}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    // The annotation names the RESOLVED path (siblings edit/write do the same).
    Assert.StartsWith($"[read {Path.GetFullPath("doc.txt")} lines 1-3 of 5 total]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", result.Content, StringComparison.Ordinal);
    Assert.Contains("2→ beta", result.Content, StringComparison.Ordinal);
    Assert.Contains("3→ gamma", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Gutter_RightAlignsToLastLineNumberWidth()
  {
    FileRead fileRead = new(["a", "b"], 10, 100);
    ReadTool tool = MakeTool(fileRead);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":9,"endLine":10}"""), ct: TestContext.Current.CancellationToken);

    // line 9 → 1 digit, line 10 → 2 digits, gutter width = 2
    Assert.Contains(" 9→ a", result.Content, StringComparison.Ordinal);  // space + 9 + arrow
    Assert.Contains("10→ b", result.Content, StringComparison.Ordinal);  // no leading space
  }

  // ---- Clamp (endLine past EOF) ----

  [Fact]
  public async Task EndLinePastEof_ClampsAndWarns()
  {
    FileRead fileRead = new(["one", "two", "three"], 3, 3);
    ReadTool tool = MakeTool(fileRead);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"small.txt","startLine":1,"endLine":100}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.StartsWith($"[read {Path.GetFullPath("small.txt")} lines 1-3 of 3 total]", result.Content, StringComparison.Ordinal);
    Assert.EndsWith("clamped", result.Content, StringComparison.Ordinal);
    Assert.Contains("[warning]", result.Content, StringComparison.Ordinal);
    Assert.Contains("100", result.Content, StringComparison.Ordinal);  // the requested endLine in warning
  }

  // ---- StartLine beyond EOF ----

  [Fact]
  public async Task StartLineBeyondEof_ReturnsError()
  {
    FileRead fileRead = new([], 0, 10);
    ReadTool tool = MakeTool(Result.Success(fileRead));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"short.txt","startLine":20,"endLine":25}"""), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
    Assert.Contains("20", result.Content, StringComparison.Ordinal);
    Assert.Contains("10", result.Content, StringComparison.Ordinal);  // file length
  }

  // ---- Empty file ----

  [Fact]
  public async Task EmptyFile_StartLine1_ReturnsError()
  {
    FileRead fileRead = new([], 0, 0);
    ReadTool tool = MakeTool(Result.Success(fileRead));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"empty.txt","startLine":1,"endLine":1}"""), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("file length (0 lines)", result.Content, StringComparison.Ordinal);
  }

  // ---- File not found ----

  [Fact]
  public async Task FileNotFound_ReturnsError()
  {
    ReadTool tool = MakeTool(Result.Failure<FileRead>(new DomainError("FileNotFound", "File not found: nope.txt")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"nope.txt","startLine":1,"endLine":5}"""), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("File not found", result.Content, StringComparison.Ordinal);
  }

  // ---- ToolDefinition ----

  [Fact]
  public void Definition_HasCorrectNameAndThreeParams()
  {
    ReadTool tool = new(new StubResolver(), new FakeFileSystemAccess(null!));

    Assert.Equal("read", tool.Definition.Name);
    Assert.Equal(4, tool.Definition.Parameters.Count);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == ToolTimeout.ParameterName && p.Minimum == 1);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "path");
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "startLine" && p.Minimum == 1);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "endLine" && p.Minimum == 1);
  }

  // ---- Path resolution ----

  [Fact]
  public async Task RelativePath_ResolvedThroughPathResolver_BeforeFileAccess()
  {
    StubResolver resolver = new();
    CapturingFileSystemAccess files = new(Result.Success(new FileRead(["alpha"], 1, 1)));
    ReadTool tool = new(resolver, files);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"doc.txt","startLine":1,"endLine":1}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("doc.txt", resolver.Requested);   // the raw model-supplied path reached the resolver
    Assert.Equal("C:\\ws\\resolved.doc.txt", files.ReceivedPath);  // the resolver output reached the file access
    Assert.StartsWith("[read C:\\ws\\resolved.doc.txt lines 1-1 of 1 total]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task PathOutsideWorkspace_ResolverFailure_SurfacesAsError()
  {
    ReadTool tool = MakeTool(Result.Failure<FileRead>(new DomainError("FileNotFound", "unreachable")),
        new ThrowingResolver());

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"..\\escape.txt","startLine":1,"endLine":1}"""), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
    Assert.Contains("outside the workspace", result.Content, StringComparison.Ordinal);
  }

  // ---- Helpers ----

  private sealed class CapturingFileSystemAccess(Result<FileRead> result) : IFileSystemAccess
  {
    private readonly Result<FileRead> _result = result;

    public string? ReceivedPath { get; private set; }

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
      ReceivedPath = path;
      return Task.FromResult(_result);
    }
  }

  private sealed class StubResolver : IPathResolver
  {
    public string? Requested { get; private set; }
    public string Returned { get; private set; } = "C:\\ws\\resolved.doc.txt";

    public Result<string> Resolve(string path)
    {
      Requested = path;
      return Result.Success(Returned);
    }
  }

  private sealed class ThrowingResolver : IPathResolver
  {
    public Result<string> Resolve(string path) => Result.Failure<string>(
        new DomainError("PathOutsideWorkspace",
            "'..\\escape.txt' resolves to 'C:\\elsewhere\\escape.txt', which is outside the workspace 'C:\\ws'. Use a path inside the workspace."));
  }

  private sealed class FakeFileSystemAccess(Result<FileRead> result) : IFileSystemAccess
  {
    private readonly Result<FileRead> _result = result;

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(_result);
  }
}
