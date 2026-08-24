using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecToolTests
{
    private readonly ExecOptions _options = ExecOptions.Default;

    [Fact]
    public void Definition_IsExec_OneRequiredProgramParameter()
    {
        var tool = CreateTool();

        Assert.Equal("exec", tool.Definition.Name);
        Assert.Equal(["timeoutSeconds", "program"],
            tool.Definition.Parameters.Select(p => p.Name).ToArray());
        Assert.Equal(ToolParameterType.String,
            tool.Definition.Parameters.Single(p => p.Name == "program").Type);
    }

    [Fact]
    public void Definition_DescribesFormatContractVerbatim()
    {
        var tool = CreateTool();

        Assert.Contains("exec error [ExecParseError]:", tool.Definition.Description);
        Assert.Contains("[exec:artifact", tool.Definition.Description);
        Assert.Contains("Tools.Invoke(", tool.Definition.Description);
        Assert.Contains("Tools.List()", tool.Definition.Description);
        Assert.Contains("exec error [ExecTimeout]:", tool.Definition.Description);
    }

    [Fact]
    public async Task MalformedJsonArguments_ReturnsError_DoesNotCallEngine()
    {
        var engine = new FakeExecEngine();
        var tool = CreateTool(engine);

        var result = await tool.ExecuteAsync(new RawToolInput("exec", "not json"));

        Assert.True(result.IsError);
        Assert.Contains("Error [InvalidJsonArguments]:", result.Content);
        Assert.Empty(engine.ValidateCalls);
    }

    [Fact]
    public async Task OversizedProgram_ReturnsExecProgramTooLarge_DoesNotValidate()
    {
        var engine = new FakeExecEngine();
        var options = new ExecOptions { MaxProgramChars = 5 };
        var tool = CreateTool(engine, options);

        var result = await tool.ExecuteAsync(
            new RawToolInput("exec", "{\"timeoutSeconds\":120,\"program\":\"abcdef\"}"));

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecProgramTooLarge]:", result.Content);
        Assert.Empty(engine.ValidateCalls);
    }

    [Fact]
    public async Task ParseErrors_ShortCircuit_BeforeExecution()
    {
        var engine = new FakeExecEngine();
        engine.ParseErrors.Add(new ExecParseError(1, 1, "syntax"));
        var tool = CreateTool(engine);

        var result = await tool.ExecuteAsync(
            new RawToolInput("exec", "{\"timeoutSeconds\":120,\"program\":\"if (x {\"}"));

        Assert.True(result.IsError);
        Assert.Contains("exec error [ExecParseError]:", result.Content);
        Assert.Empty(engine.ExecuteCalls);
    }

    [Fact]
    public async Task CompletedRun_FormatsAndRecordsActivity()
    {
        var engine = new FakeExecEngine();
        var activity = new RecordingActivitySink();
        var tool = CreateTool(engine, activity: activity);

        var result = await tool.ExecuteAsync(
            new RawToolInput("exec", "{\"timeoutSeconds\":120,\"program\":\"Write-Output 'hi'\"}"));

        Assert.False(result.IsError);
        Assert.Equal("hi", result.Content);
        var record = Assert.Single(activity.Records);
        Assert.Equal(ExecRunStatus.Completed, record.Status);
        Assert.False(record.IsError);
        Assert.Equal(2, record.OutputChars);
    }

    [Fact]
    public async Task Overflow_ArtifactStoreCalled_ArtifactLineInResult()
    {
        var engine = new FakeExecEngine();
        engine.Output = new string('x', 60 * 1024);
        var store = new FakeOutputStore("C:\\art\\out.txt");
        var tool = CreateTool(engine, artifacts: store);

        var result = await tool.ExecuteAsync(
            new RawToolInput("exec", "{\"timeoutSeconds\":120,\"program\":\"x\"}"));

        Assert.Equal(60 * 1024, store.Written.Length);
        Assert.Contains("[exec:artifact C:\\art\\out.txt]", result.Content);
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

        public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct)
        {
            ValidateCalls.Add(program.Text);
            return Task.FromResult(Result<IReadOnlyList<ExecParseError>>.Success(ParseErrors.ToList()));
        }

        public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct)
        {
            ExecuteCalls.Add(program.Text);
            return Task.FromResult(ExecRunResult.Completed(Output));
        }
    }

    private sealed class FakeOutputStore : IExecOutputStore
    {
        private readonly string _path;
        public string Written = "";
        public FakeOutputStore(string path) => _path = path;
        public Task<string> WriteAsync(string content, CancellationToken ct)
        {
            Written = content;
            return Task.FromResult(_path);
        }
    }

    private sealed class RecordingActivitySink : IExecActivitySink
    {
        public List<ExecActivity> Records { get; } = [];
        public Task RecordAsync(ExecActivity activity, CancellationToken ct)
        {
            Records.Add(activity);
            return Task.CompletedTask;
        }
    }
}
