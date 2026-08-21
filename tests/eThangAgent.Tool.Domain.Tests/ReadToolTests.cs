using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Xunit.Abstractions;

namespace eThangAgent.ToolDomain.Tests;

public class ReadToolTests
{
    private readonly ITestOutputHelper _out;
    public ReadToolTests(ITestOutputHelper @out) => _out = @out;

    private static ReadTool MakeTool(Result<FileRead> readResult) =>
        new(new FakeFileSystemAccess(readResult));

    private static ReadTool MakeTool(FileRead success) =>
        new(new FakeFileSystemAccess(Result<FileRead>.Success(success)));

    // ---- JSON parsing ----

    [Fact]
    public async Task RawArguments_NotJson_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read", "not json"));
        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task RawArguments_NotObject_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read", "[1,2,3]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    // ---- Missing parameters ----

    [Fact]
    public async Task MissingPath_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"startLine":1,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("Missing required", result.Content);
        Assert.Contains("path", result.Content);
    }

    [Fact]
    public async Task MissingStartLine_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
    }

    [Fact]
    public async Task MissingEndLine_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1}"""));
        Assert.True(result.IsError);
        Assert.Contains("endLine", result.Content);
    }

    // ---- Wrong types ----

    [Fact]
    public async Task StartLineIsString_ReturnsTypeError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":"1","endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartLineIsFloat_ReturnsTypeError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1.5,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("integer", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PathIsNumber_ReturnsTypeError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":123,"startLine":1,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("path", result.Content);
        Assert.Contains("string", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Unknown parameters ----

    [Fact]
    public async Task ExtraParameter_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1,"endLine":5,"encoding":"utf16"}"""));
        Assert.True(result.IsError);
        Assert.Contains("encoding", result.Content);
        Assert.Contains("Unknown parameter", result.Content);
    }

    // ---- Value constraints ----

    [Fact]
    public async Task StartLine_LessThan1_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":0,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("≥ 1", result.Content);
    }

    [Fact]
    public async Task StartLine_GreaterThanEndLine_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":10,"endLine":5}"""));
        Assert.True(result.IsError);
        Assert.Contains("must not exceed", result.Content);
    }

    // ---- Range cap ----

    [Fact]
    public async Task RangeExceeds1000_ReturnsError()
    {
        var tool = MakeTool((Result<FileRead>)null!);
        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":1,"endLine":2000}"""));
        Assert.True(result.IsError);
        Assert.Contains("1000", result.Content);
        Assert.Contains("chunks", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Execution (happy path) ----

    [Fact]
    public async Task SuccessfulRead_ReturnsFormattedContent()
    {
        var fileRead = new FileRead(["alpha", "beta", "gamma"], 3, 5);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"doc.txt","startLine":1,"endLine":3}"""));

        Assert.False(result.IsError);
        Assert.StartsWith("[read doc.txt lines 1-3 of 5 total]", result.Content);
        Assert.Contains("1→ alpha", result.Content);
        Assert.Contains("2→ beta", result.Content);
        Assert.Contains("3→ gamma", result.Content);
    }

    [Fact]
    public async Task Gutter_RightAlignsToLastLineNumberWidth()
    {
        var fileRead = new FileRead(["a", "b"], 10, 100);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"f","startLine":9,"endLine":10}"""));

        // line 9 → 1 digit, line 10 → 2 digits, gutter width = 2
        Assert.Contains(" 9→ a", result.Content);  // space + 9 + arrow
        Assert.Contains("10→ b", result.Content);  // no leading space
    }

    // ---- Clamp (endLine past EOF) ----

    [Fact]
    public async Task EndLinePastEof_ClampsAndWarns()
    {
        var fileRead = new FileRead(["one", "two", "three"], 3, 3);
        var tool = MakeTool(fileRead);

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"small.txt","startLine":1,"endLine":100}"""));

        Assert.False(result.IsError);
        Assert.StartsWith("[read small.txt lines 1-3 of 3 total]", result.Content);
        Assert.EndsWith("clamped", result.Content);
        Assert.Contains("[warning]", result.Content);
        Assert.Contains("100", result.Content);  // the requested endLine in warning
    }

    // ---- StartLine beyond EOF ----

    [Fact]
    public async Task StartLineBeyondEof_ReturnsError()
    {
        var fileRead = new FileRead([], 0, 10);
        var tool = MakeTool(Result<FileRead>.Success(fileRead));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"short.txt","startLine":20,"endLine":25}"""));

        Assert.True(result.IsError);
        Assert.Contains("startLine", result.Content);
        Assert.Contains("20", result.Content);
        Assert.Contains("10", result.Content);  // file length
    }

    // ---- Empty file ----

    [Fact]
    public async Task EmptyFile_StartLine1_ReturnsError()
    {
        var fileRead = new FileRead([], 0, 0);
        var tool = MakeTool(Result<FileRead>.Success(fileRead));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"empty.txt","startLine":1,"endLine":1}"""));

        Assert.True(result.IsError);
        Assert.Contains("file length (0 lines)", result.Content);
    }

    // ---- File not found ----

    [Fact]
    public async Task FileNotFound_ReturnsError()
    {
        var tool = MakeTool(Result<FileRead>.Failure(new Error("FileNotFound", "File not found: nope.txt")));

        var result = await tool.ExecuteAsync(new RawToolInput("read",
            """{"path":"nope.txt","startLine":1,"endLine":5}"""));

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content);
    }

    // ---- ToolDefinition ----

    [Fact]
    public void Definition_HasCorrectNameAndThreeParams()
    {
        var tool = new ReadTool(new FakeFileSystemAccess(null!));

        Assert.Equal("read", tool.Definition.Name);
        Assert.Equal(3, tool.Definition.Parameters.Count);
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "path");
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "startLine" && p.Minimum == 1);
        Assert.Contains(tool.Definition.Parameters, p => p.Name == "endLine" && p.Minimum == 1);
    }

    // ---- Helpers ----

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        private readonly Result<FileRead> _result;
        public FakeFileSystemAccess(Result<FileRead> result) => _result = result;
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
            => Task.FromResult(_result);
    }
}
