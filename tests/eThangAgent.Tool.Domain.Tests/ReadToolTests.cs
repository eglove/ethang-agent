using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class ReadToolTests
{
  private static ReadTool MakeTool(Result<FileRead> readResult) =>
      new(new FakeFileSystemAccess(readResult));

  private static ReadTool MakeTool(FileRead success) =>
      new(new FakeFileSystemAccess(Result.Success(success)));

  // ---- JSON parsing ----

  [Fact]
  public async Task RawArguments_NotJson_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read", "not json"));
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RawArguments_NotObject_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read", "[1,2,3]"));
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
                                 """{"timeoutSeconds":120,"startLine":1,"endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","endLine":5}"""));
    Assert.True(result.IsError);
    Assert.Contains("startLine", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingEndLine_ReturnsError()
  {
    ReadTool tool = MakeTool((Result<FileRead>)null!);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("read",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"path":"f","startLine":1}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":"1","endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":1.5,"endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":123,"startLine":1,"endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":5,"encoding":"utf16"}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":0,"endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":10,"endLine":5}"""));
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2000}"""));
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
                                 """{"timeoutSeconds":120,"path":"doc.txt","startLine":1,"endLine":3}"""));

    Assert.False(result.IsError);
    Assert.StartsWith("[read doc.txt lines 1-3 of 5 total]", result.Content, StringComparison.Ordinal);
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
                                 """{"timeoutSeconds":120,"path":"f","startLine":9,"endLine":10}"""));

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
                                 """{"timeoutSeconds":120,"path":"small.txt","startLine":1,"endLine":100}"""));

    Assert.False(result.IsError);
    Assert.StartsWith("[read small.txt lines 1-3 of 3 total]", result.Content, StringComparison.Ordinal);
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
                                 """{"timeoutSeconds":120,"path":"short.txt","startLine":20,"endLine":25}"""));

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
                                 """{"timeoutSeconds":120,"path":"empty.txt","startLine":1,"endLine":1}"""));

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
                                 """{"timeoutSeconds":120,"path":"nope.txt","startLine":1,"endLine":5}"""));

    Assert.True(result.IsError);
    Assert.Contains("File not found", result.Content, StringComparison.Ordinal);
  }

  // ---- ToolDefinition ----

  [Fact]
  public void Definition_HasCorrectNameAndThreeParams()
  {
    ReadTool tool = new(new FakeFileSystemAccess(null!));

    Assert.Equal("read", tool.Definition.Name);
    Assert.Equal(4, tool.Definition.Parameters.Count);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == ToolTimeout.ParameterName && p.Minimum == 1);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "path");
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "startLine" && p.Minimum == 1);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "endLine" && p.Minimum == 1);
  }

  // ---- Helpers ----

  private sealed class FakeFileSystemAccess(Result<FileRead> result) : IFileSystemAccess
  {
    private readonly Result<FileRead> _result = result;

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(_result);
  }
}
