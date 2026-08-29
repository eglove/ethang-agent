using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecToolTests
{
  private readonly ExecOptions _options = ExecOptions.Default;

  [Fact]
  public void Definition_IsExec_OneRequiredProgramParameter()
  {
    ExecTool tool = CreateTool();

    Assert.Equal("exec", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "program"],
        [.. tool.Definition.Parameters.Select(p => p.Name)]);
    Assert.Equal(ToolParameterType.Text,
        tool.Definition.Parameters.Single(p => p.Name == "program").Type);
  }

  [Fact]
  public void Definition_DescribesFormatContractVerbatim()
  {
    ExecTool tool = CreateTool();

    Assert.Contains("exec error [ExecParseError]:", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("[exec:artifact", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("Tools.Invoke(", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("Tools.List()", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds is the only execution budget", tool.Definition.Description, StringComparison.Ordinal);
    Assert.Contains("Error [ToolTimeout]:", tool.Definition.Description, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedJsonArguments_ReturnsError_DoesNotCallEngine()
  {
    FakeExecEngine engine = new();
    ExecTool tool = CreateTool(engine);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", "not json"));

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidJsonArguments]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(engine.ValidateCalls);
  }

  [Fact]
  public async Task OversizedProgram_ReturnsExecProgramTooLarge_DoesNotValidate()
  {
    FakeExecEngine engine = new();
    ExecOptions options = new() { MaxProgramChars = 5 };
    ExecTool tool = CreateTool(engine, options);

    ToolResult result = await tool.ExecuteAsync(
            new RawToolInput("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"program\":\"abcdef\"}"));

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecProgramTooLarge]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(engine.ValidateCalls);
  }

  [Fact]
  public async Task ParseErrors_ShortCircuit_BeforeExecution()
  {
    FakeExecEngine engine = new();
    engine.ParseErrors.Add(new ExecParseError(1, 1, "syntax"));
    ExecTool tool = CreateTool(engine);

    ToolResult result = await tool.ExecuteAsync(
            new RawToolInput("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"program\":\"if (x {\"}"));

    Assert.True(result.IsError);
    Assert.Contains("exec error [ExecParseError]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(engine.ExecuteCalls);
  }

  [Fact]
  public async Task CompletedRun_FormatsAndRecordsActivity()
  {
    FakeExecEngine engine = new();
    RecordingActivitySink activity = new();
    ExecTool tool = CreateTool(engine, activity: activity);

    ToolResult result = await tool.ExecuteAsync(
            new RawToolInput("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"program\":\"Write-Output 'hi'\"}"));

    Assert.False(result.IsError);
    Assert.Equal("hi", result.Content);
    ExecActivity record = Assert.Single(activity.Records);
    Assert.Equal(ExecRunStatus.Completed, record.Status);
    Assert.False(record.IsError);
    Assert.Equal(2, record.OutputChars);
  }

  [Fact]
  public async Task Overflow_ArtifactStoreCalled_ArtifactLineInResult()
  {
    FakeExecEngine engine = new()
    {
      Output = new string('x', 60 * 1024)
    };
    FakeOutputStore store = new("C:\\art\\out.txt");
    ExecTool tool = CreateTool(engine, artifacts: store);

    ToolResult result = await tool.ExecuteAsync(
            new RawToolInput("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":120,\"program\":\"x\"}"));

    Assert.Equal(60 * 1024, store.Written.Length);
    Assert.Contains("[exec:artifact C:\\art\\out.txt]", result.Content, StringComparison.Ordinal);
  }

  private ExecTool CreateTool(
      FakeExecEngine? engine = null,
      ExecOptions? options = null,
      FakeOutputStore? artifacts = null,
      IExecActivitySink? activity = null)
      => new(engine ?? new FakeExecEngine(), options ?? _options,
          artifacts ?? new FakeOutputStore(""), activity ?? NullExecActivitySink.Instance);

  private sealed class FakeExecEngine : IExecEngine
  {
    public List<string> ValidateCalls { get; } = [];
    public List<string> ExecuteCalls { get; } = [];
    public List<ExecParseError> ParseErrors { get; } = [];
    public string Output { get; set; } = "hi";

    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default)
    {
      ValidateCalls.Add(program.Text);
      return Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([.. ParseErrors]));
    }

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
    {
      ExecuteCalls.Add(program.Text);
      return Task.FromResult(ExecRunResult.Completed(Output));
    }
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

  private sealed class RecordingActivitySink : IExecActivitySink
  {
    public List<ExecActivity> Records { get; } = [];
    public Task RecordAsync(ExecActivity activity, CancellationToken ct = default)
    {
      Records.Add(activity);
      return Task.CompletedTask;
    }
  }
}
