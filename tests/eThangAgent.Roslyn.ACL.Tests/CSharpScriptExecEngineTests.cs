using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

public class CSharpScriptExecEngineTests
{
    /// <summary>Tests exercise engine semantics against a fixed workspace root — the
    ///     same contract the composition supplies per session (IWorkspaceContext).</summary>
    private static CSharpScriptExecEngine CreateEngine(ExecOptions? options = null)
        => new(CapabilityRegistry.Create([]), options ?? ExecOptions.Default,
            workspaceRoot: () => AppContext.BaseDirectory);

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
    [Fact]
    public async Task Workspace_Global_Reflects_Injected_Resolver_PerExecution()
    {
        string current = AppContext.BaseDirectory;
        var engine = new CSharpScriptExecEngine(CapabilityRegistry.Create([]), ExecOptions.Default,
            workspaceRoot: () => current);
        var first = await engine.ExecuteAsync(new ExecProgram("return Workspace;"));
        Assert.Equal(current, first.Output);

        // A second execution sees the resolver's NEW value — the construction-time
        // capture that pinned multi-session hosts to one root is gone.
        current = Path.GetTempPath();
        var second = await engine.ExecuteAsync(new ExecProgram("return Workspace;"));
        Assert.Equal(Path.GetTempPath(), second.Output);
    }
}

public class CSharpEvidenceRunnerTests
{
    [Fact]
    public async Task TrueExpression_ReturnsConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("1 + 1 == 2");

        Assert.True(r.Confirmed);
        Assert.Empty(r.Detail);
    }

    [Fact]
    public async Task FalseExpression_ReturnsNotConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("1 == 2");

        Assert.False(r.Confirmed);
        Assert.NotEmpty(r.Detail);
    }

    [Fact]
    public async Task Exception_ReturnsNotConfirmed()
    {
        var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
        var r = await runner.RunAsync("throw new System.Exception(\"fail\")");

        Assert.False(r.Confirmed);
        Assert.Contains("fail", r.Detail);
    }

    [Fact]
    public async Task FileExists_Evidence_Works()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var runner = new CSharpEvidenceRunner(EvidenceOptions.Default);
            var r = await runner.RunAsync($"System.IO.File.Exists(@\"{tmp.Replace("\\", "\\\\")}\")");
            Assert.True(r.Confirmed);
        }
        finally { File.Delete(tmp); }
    }
}
