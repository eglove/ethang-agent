using System.Diagnostics;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ExecTool(IExecEngine engine, ExecOptions options, IExecOutputStore artifacts,
    IExecActivitySink activity) : ITool
{
  public const string ToolName = "exec";

  private readonly IExecEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
  private readonly ExecOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IExecOutputStore _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
  private readonly IExecActivitySink _activity = activity ?? throw new ArgumentNullException(nameof(activity));

  public ToolDefinition Definition { get; } = new(
      ToolName,
      """
        Execute a C# program in the agent workspace. The script runs in-process via Roslyn scripting. The return value is the result: strings verbatim, other objects as one-line JSON. Call Output() during execution for intermediate output. Console.WriteLine is also captured. Thrown exceptions mark the result as an error with exec error [ScriptError] lines. Output over 50,000 characters is truncated with both ends preserved and the full output saved to a file reported as [exec:artifact <path>] — read that file with the read tool. Compile errors report 'exec error [ExecParseError]:' followed by 'line N, col M: message' entries. timeoutSeconds is the only execution budget: when it elapses the call fails with 'Error [ToolTimeout]:'. Tools are available as methods on the Tools object taking one anonymous object: Tools.read(new { path = "file.txt", startLine = 1, endLine = 50 }). Tools.Invoke("name", args) is the generic form. Tools.List() lists available tools. Shell(exe, args...) runs an external command line spawned directly via native .NET process APIs (no shell intermediary): every argument after the exe is one token of a single native command line (a multi-token piece like "build -c Release" is re-parsed into separate tokens with Windows argv rules), and the native exit code propagates verbatim. Nested exec is not available. Malformed arguments to exec itself report 'Error [Code]: ...'.
        """,
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("program", ToolParameterType.Text,
                "The C# program text to execute."),
      ],
      ["timeoutSeconds", "program"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ExecToolInput> parsed = ExecToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"Error [{parsed.Error!.Code}]: {parsed.Error.Message}", true));
    }

    Result<ExecProgram> program = ExecProgram.Create(parsed.Value!.Program, _options);
    if (!program.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"exec error [{program.Error!.Code}]: {program.Error.Message}", true));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!budget.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"Error [{budget.Error!.Code}]: {budget.Error.Message}", true));
    }

    ExecProgram exec = program.Value!;
    return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
        RunAsync(exec, token), ct);
  }

  private async Task<ToolResult> RunAsync(ExecProgram exec, CancellationToken ct)
  {
    Result<IReadOnlyList<ExecParseError>> parse = await _engine.ValidateAsync(exec, ct).ConfigureAwait(false);
    if (!parse.IsSuccess)
    {
      return new ToolResult($"Error [{parse.Error!.Code}]: {parse.Error.Message}", true);
    }

    if (parse.Value!.Count > 0)
    {
      return ExecResultFormatter.ParseErrors(parse.Value!, _options.MaxParseErrors);
    }

    long started = Stopwatch.GetTimestamp();
    ExecRunResult run = await _engine.ExecuteAsync(exec, ct).ConfigureAwait(false);

    string? artifactPath = null;
    if (run.Status == ExecRunStatus.Completed && run.Output.Length > _options.MaxOutputChars)
    {
      artifactPath = await _artifacts.WriteAsync(run.Output, ct).ConfigureAwait(false);
    }

    ToolResult result = ExecResultFormatter.Format(run, _options, artifactPath);
    await _activity.RecordAsync(new ExecActivity(
        exec.Text.Length > 80 ? exec.Text[..80] : exec.Text,
        run.Status,
        run.Output.Length,
        Stopwatch.GetElapsedTime(started),
        result.IsError), ct).ConfigureAwait(false);
    return result;
  }
}
