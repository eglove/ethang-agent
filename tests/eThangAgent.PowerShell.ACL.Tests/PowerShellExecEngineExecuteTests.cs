using eThangAgent.PowerShell.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL.Tests;

public class PowerShellExecEngineExecuteTests
{
    private PowerShellExecEngine CreateEngine(ExecOptions? options = null,
        IFileSystemAccess? files = null)
        => new(new ToolRegistry([
            new ReadTool(files ?? new FakeFileSystemAccess()),
            new NamedFakeTool(ExecTool.ToolName)]),
            options ?? ExecOptions.Default);

    [Fact]
    public async Task ScriptOutput_ReturnsCompletedContent()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram("Write-Output 'hello'"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Equal("hello", run.Output);
        Assert.Empty(run.ErrorLines);
    }

    [Fact]
    public async Task HashtableOutput_RenderedAsOneLineJson()
    {
        var run = await CreateEngine().ExecuteAsync(
            new ExecProgram("Write-Output @{ a = 1 }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("\"a\":1", run.Output);
    }

    [Fact]
    public async Task MultiLineOutput_JoinedWithNewlines()
    {
        var run = await CreateEngine().ExecuteAsync(
            new ExecProgram("1..3 | ForEach-Object { Write-Output (\"n\" + $_) }"));

        Assert.Equal("n1\nn2\nn3", run.Output);
    }

    [Fact]
    public async Task ReadWrapper_InsideScript_CallsTool()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram(
            "read @{ path = 'x.txt'; startLine = 1; endLine = 2 }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Empty(run.ErrorLines);
        Assert.Contains("[read x.txt lines 1-2 of 2 total]", run.Output);
        Assert.Contains("alpha", run.Output);
    }

    [Fact]
    public async Task InvokeAgentTool_GenericForm_CallsTool()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram(
            "Invoke-AgentTool -Name read -ToolInput @{ path = 'x.txt'; startLine = 1; endLine = 2 }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("alpha", run.Output);
    }

    [Fact]
    public async Task GetAgentTool_ListsTools_NotExec()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram("Get-AgentTool"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("read(", run.Output);
        Assert.DoesNotContain("exec(", run.Output);
    }

    [Fact]
    public async Task ToolFailure_IsCatchableInScript()
    {
        var engine = CreateEngine(files: new FailingFileSystemAccess());

        var run = await engine.ExecuteAsync(new ExecProgram(
            "try { read @{ path = 'missing.txt'; startLine = 1; endLine = 5 } } " +
            "catch { Write-Output ('fallback: ' + $_.Exception.Message) }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Empty(run.ErrorLines);
        Assert.StartsWith("fallback:", run.Output);
        Assert.Contains("FileNotFound", run.Output);
    }

    [Fact]
    public async Task WriteError_LandsInErrorLines_ScriptContinues()
    {
        var run = await CreateEngine().ExecuteAsync(
            new ExecProgram("Write-Output 'partial'; Write-Error 'boom'"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.Contains("partial", run.Output);
        Assert.Contains("boom", run.ErrorLines);
    }

    [Fact]
    public async Task UnknownCommand_TerminatingError_InErrorLines()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram("Not-A-Real-Command"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.NotEmpty(run.ErrorLines);
    }

    [Fact]
    public async Task NoNestedExec_ExecCommandNotAvailable()
    {
        var run = await CreateEngine().ExecuteAsync(new ExecProgram(
            "exec @{ program = 'Write-Output x' }"));

        Assert.Equal(ExecRunStatus.Completed, run.Status);
        Assert.NotEmpty(run.ErrorLines);
        Assert.Contains("exec", run.ErrorLines[0]);
    }

    [Fact]
    public async Task Timeout_StopsPipeline_KeepsPartialOutput()
    {
        var options = new ExecOptions { Timeout = TimeSpan.FromMilliseconds(300) };

        var run = await CreateEngine(options).ExecuteAsync(new ExecProgram(
            "Write-Output 'started'; Start-Sleep -Seconds 300"));

        Assert.Equal(ExecRunStatus.Timeout, run.Status);
        Assert.Contains("started", run.Output);
        Assert.Contains("timed out", run.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_ReportsCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = await CreateEngine().ExecuteAsync(
            new ExecProgram("Start-Sleep -Seconds 300"), cts.Token);

        Assert.Equal(ExecRunStatus.Cancelled, run.Status);
        Assert.Contains("cancelled", run.ErrorMessage);
    }

    [Fact]
    public async Task FreshRunspace_StateDoesNotLeakBetweenCalls()
    {
        var engine = CreateEngine();

        var first = await engine.ExecuteAsync(new ExecProgram("$x = 42; Write-Output $x"));
        var second = await engine.ExecuteAsync(new ExecProgram(
            "Write-Output ($null -eq (Get-Variable -Name x -ErrorAction SilentlyContinue))"));

        Assert.Equal("42", first.Output);
        Assert.Equal("true", second.Output);
    }

    private sealed class FakeFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Success(
                new FileRead(["alpha", "beta"], 2, 2)));
    }

    private sealed class FailingFileSystemAccess : IFileSystemAccess
    {
        public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
            CancellationToken ct = default)
            => Task.FromResult(Result<FileRead>.Failure(
                new Error("FileNotFound", $"File not found: {path}.")));
    }

    private sealed class NamedFakeTool : ITool
    {
        public NamedFakeTool(string name)
            => Definition = new ToolDefinition(name, "desc", []);

        public ToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => Task.FromResult(new ToolResult("ok", false));
    }
}
