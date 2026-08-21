using System.Diagnostics;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ExecTool : ITool
{
    public const string ToolName = "exec";

    private readonly IExecEngine _engine;
    private readonly ExecOptions _options;
    private readonly IExecOutputStore _artifacts;
    private readonly IExecActivitySink _activity;

    public ToolDefinition Definition { get; } = new(
        ToolName,
        """
        Execute a PowerShell program in the agent workspace. The program's output stream is the result: each emitted object appears on its own line (strings verbatim, complex objects as one-line JSON). Write-Error lines appear as 'exec error [ScriptError]: ...'; any terminating or non-terminating error marks the result as an error. Output over 50,000 characters is truncated with both ends preserved and the full output saved to a file reported as [exec:artifact <path>] — read that file with the read tool. Validation failures report 'exec error [ExecParseError]:' followed by 'line N, col M: message' entries. Timeouts (120s) report 'exec error [ExecTimeout]:' with partial output. Tools are available as functions taking one hashtable: read @{ path = 'file.txt'; startLine = 1; endLine = 50 }. Invoke-AgentTool -Name <tool> -ToolInput <hashtable> is the generic form. Get-AgentTool lists available tools. Nested exec is not available. Malformed arguments to exec itself report 'Error [Code]: ...'.
        """,
        [new ToolParameter("program", ToolParameterType.String,
            "The PowerShell program text to execute.")]);

    public ExecTool(IExecEngine engine, ExecOptions options, IExecOutputStore artifacts,
        IExecActivitySink activity)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = ExecToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return new ToolResult($"Error [{parsed.Error!.Code}]: {parsed.Error.Message}", true);

        var program = ExecProgram.Create(parsed.Value!.Program, _options);
        if (!program.IsSuccess)
            return new ToolResult($"exec error [{program.Error!.Code}]: {program.Error.Message}", true);

        var exec = program.Value!;
        var parse = await _engine.ValidateAsync(exec, ct);
        if (!parse.IsSuccess)
            return new ToolResult($"Error [{parse.Error!.Code}]: {parse.Error.Message}", true);
        if (parse.Value!.Count > 0)
            return ExecResultFormatter.ParseErrors(parse.Value!, _options.MaxParseErrors);

        var started = Stopwatch.GetTimestamp();
        var run = await _engine.ExecuteAsync(exec, ct);

        string? artifactPath = null;
        if (run.Status == ExecRunStatus.Completed && run.Output.Length > _options.MaxOutputChars)
            artifactPath = await _artifacts.WriteAsync(run.Output, ct);

        var result = ExecResultFormatter.Format(run, _options, artifactPath);
        await _activity.RecordAsync(new ExecActivity(
            exec.Text.Length > 80 ? exec.Text[..80] : exec.Text,
            run.Status,
            run.Output.Length,
            Stopwatch.GetElapsedTime(started),
            result.IsError), ct);
        return result;
    }
}
