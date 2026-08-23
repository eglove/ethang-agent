using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

public class CSharpScriptExecEngineTests
{
    private static CSharpScriptExecEngine CreateEngine(ExecOptions? options = null)
        => new(CapabilityRegistry.Create([]), options ?? ExecOptions.Default);

    [Fact]
    public async Task StringReturnValue_BecomesOutput()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("\"hello from csharp\""));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("hello from csharp", run.Output);
        Assert.Empty(run.ErrorLines);
    }

    [Fact]
    public async Task IntReturnValue_SerializedToJson()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("42"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("42", run.Output);
    }

    [Fact]
    public async Task VoidScript_ReturnsEmptyOutput()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("var x = 1 + 1;"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("", run.Output);
    }

    [Fact]
    public async Task Output_CapturesLinesDuringExecution()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("Output(\"line1\"); Output(\"line2\"); 0"));

        Assert.Contains("line1", run.Output);
        Assert.Contains("line2", run.Output);
    }

    [Fact]
    public async Task CompileError_ReturnsInValidate()
    {
        var engine = CreateEngine();
        var errors = await engine.ValidateAsync(new ExecProgram("this is not valid csharp ??!!"));

        Assert.True(errors.IsSuccess);
        Assert.NotEmpty(errors.Value!);
    }

    [Fact]
    public async Task RuntimeException_BecomesError()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("throw new System.Exception(\"boom\");"));

        Assert.Equal(ExecRunStatus.Completed, run.Status); // completed with error lines
        Assert.NotEmpty(run.ErrorLines);
        Assert.Contains("boom", run.ErrorLines[0]);
    }

    [Fact]
    public async Task Shell_RunsCommand()
    {
        var engine = CreateEngine();
        var run = await engine.ExecuteAsync(new ExecProgram("var r = Shell(\"cmd\", \"/c\", \"echo hello\"); return r.Stdout;"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("hello", run.Output);
    }
}
