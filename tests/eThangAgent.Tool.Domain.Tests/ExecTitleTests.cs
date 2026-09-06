using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

/// <summary>Grand-plan tool-output item: the exec call's header gains a required
///     title input, and the tool result carries that title plus the program rendered
///     as a fenced C# body for the host UI. The model reads Title/DisplayBody as
///     metadata; the content contract is unchanged.</summary>
public class ExecTitleTests
{
  private readonly ExecOptions _options = ExecOptions.Default;

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Input_BlankTitle_IsRejected(string title)
  {
    string json = System.Text.Json.JsonSerializer.Serialize(new { timeoutSeconds = 5, title, program = "return 1;" });

    Result<ExecToolInput> parsed = ExecToolInput.Create(json);

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidParameterValue", parsed.Error.Code);
  }

  [Fact]
  public void Input_MissingTitle_IsRejectedWithMissingParameter()
  {
    Result<ExecToolInput> parsed = ExecToolInput.Create(
        /*lang=json,strict*/ "{\"timeoutSeconds\":5,\"program\":\"return 1;\"}");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("MissingParameter", parsed.Error.Code);
    Assert.Contains("title", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Input_TitleIsCarriedNextToProgram()
  {
    Result<ExecToolInput> parsed = ExecToolInput.Create(
        /*lang=json,strict*/ "{\"timeoutSeconds\":5,\"title\":\"parse names\",\"program\":\"return 1;\"}");

    Assert.True(parsed.IsSuccess);
    Assert.Equal("parse names", parsed.Value.Title);
    Assert.Equal("return 1;", parsed.Value.Program);
  }

  [Fact]
  public void Definition_AdvertisesTitle_AndRequiresIt()
  {
    ExecTool tool = CreateTool();

    ToolParameter title = tool.Definition.Parameters.Single(p => p.Name == "title");
    Assert.Equal(ToolParameterType.Text, title.Type);
    Assert.Equal(["timeoutSeconds", "title", "program"], [.. tool.Definition.RequiredParameters]);
    Assert.Contains("title", tool.Definition.Description, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CompletedRun_ResultCarriesTitle_AndFencedProgramBody()
  {
    FakeExecEngine engine = new();
    ExecTool tool = CreateTool(engine);
    string json = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 5,
      title = "parse names",
      program = "return 42;"
    });

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", json), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("parse names", result.Title);
    Assert.NotNull(result.DisplayBody);
    Assert.Contains("```csharp", result.DisplayBody, StringComparison.Ordinal);
    Assert.Contains("return 42;", result.DisplayBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedJson_ErrorResult_CarriesNoTitleOrBody()
  {
    ExecTool tool = CreateTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", "not json"), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Null(result.Title);
    Assert.Null(result.DisplayBody);
  }

  private ExecTool CreateTool(FakeExecEngine? engine = null)
      => new(engine ?? new FakeExecEngine(), _options, new FakeOutputStore(""), NullExecActivitySink.Instance);

  private sealed class FakeExecEngine : IExecEngine
  {
    public string Output { get; set; } = "hi";

    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(ExecRunResult.Completed(Output));
  }

  private sealed class FakeOutputStore(string path) : IExecOutputStore
  {
    private readonly string _path = path;

    public string Written { get; private set; } = "";

    public Task<string> WriteAsync(string content, CancellationToken ct = default)
    {
      Written = content;
      return Task.FromResult(_path);
    }
  }
}
