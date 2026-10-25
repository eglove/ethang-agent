using System.Diagnostics;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class ExecTool(IExecEngine engine, ExecOptions options,
    IExecActivitySink activity) : ITool, IToolCallIdentifiable
{
  public const string ToolName = "exec";

  private readonly IExecEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));
  private readonly ExecOptions _options = options ?? throw new ArgumentNullException(nameof(options));
  private readonly IExecActivitySink _activity = activity ?? throw new ArgumentNullException(nameof(activity));

  /// <summary>The SEMANTIC call identity for repeat-guard observation (issue #124,
  ///     2026-10-15 incident): the program body alone. The envelope's title and
  ///     timeoutSeconds are transport metadata the model free-varies; keying identity on
  ///     the raw arguments let one identical successful call run 195 times while the
  ///     guard saw only fresh calls. Malformed arguments parse to null — the caller
  ///     falls back to the raw arguments and the guard behaves as before.</summary>
  public static string? CallIdentity(string jsonArguments)
  {
    try
    {
      using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonArguments);
      return doc.RootElement.ValueKind is System.Text.Json.JsonValueKind.Object
              && doc.RootElement.TryGetProperty("program", out System.Text.Json.JsonElement program)
              && program.ValueKind is System.Text.Json.JsonValueKind.String
          ? program.GetString()
          : null;
    }
    catch (System.Text.Json.JsonException)
    {
      return null;
    }
  }

  public ToolDefinition Definition { get; } = new(
      ToolName,
      """
        Execute a C# program in the agent workspace. THIS IS THE ONLY TOOL: every other capability (read, write, edit, web_fetch, mcp, docx, pdf, ...) is reached as Tools.* inside the program — call Tools.List() to see them and Tools.Describe("<name>") for one tool's contract. The required title names this run for the session UI: a short imperative label for the program's purpose (for example "count failing tests"), not the program text. The transcript's tool card header shows the title and the expanded body shows the program. The script runs in-process via Roslyn scripting. The return value is the result: strings verbatim, other objects as one-line JSON. Call Output() during execution for intermediate output. Console.WriteLine is also captured. Thrown exceptions mark the result as an error with exec error [ScriptError] lines. Compile errors report 'exec error [ExecParseError]:' followed by 'line N, col M: message' entries. timeoutSeconds is the only execution budget: when it elapses the call fails with 'Error [ToolTimeout]:'. Tools are available as methods on the Tools object taking one anonymous object: Tools.read(new { path = "file.txt", startLine = 1, endLine = 50 }). Tools.Invoke("name", args) is the generic form. Tools.List() lists available tools. Shell(exe, args...) runs an external command line spawned directly via native .NET process APIs (no shell intermediary): every argument after the exe is ONE native argument, passed verbatim — an argument containing spaces (a path, a commit message) reaches the target as a single argument; do not pack several tokens into one argument — and the native exit code propagates verbatim. Nested exec is not available. Native interop is not available: a program declaring DllImport/LibraryImport, an extern method, NativeLibrary.Load/GetExport, or Marshal.GetDelegateForFunctionPointer fails validation with 'Error [NativeInteropBlocked]' — the native desktop surface (mouse, keyboard, focus, window geometry) belongs to the computer tool, which runs it in a killable, audited broker process. Use computer with action observe/click instead. Malformed arguments to exec itself report 'Error [Code]: ...'. A program that references Directory.GetCurrentDirectory() gets the app's launch directory, not the workspace; such runs carry a '[exec: workspace note — ...]' annotation stating both paths. A RELATIVE path in a raw File.*/Directory.* call resolves against that same launch directory, NOT the workspace: build it from Workspace (Path.Combine(Workspace, "notes.txt")) or use ResolvePath("notes.txt"), which resolves at the workspace and refuses a path that escapes it.
        """,
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("title", ToolParameterType.Text,
                "Short imperative label naming this run for the transcript's tool card (for example: count failing tests)."),
            new ToolParameter("program", ToolParameterType.Text,
                "The C# program text to execute."),
      ],
      ["timeoutSeconds", "title", "program"]);

  string? IToolCallIdentifiable.CallIdentity(string jsonArguments) => CallIdentity(jsonArguments);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ExecToolInput> parsed = ExecToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"Error [{parsed.Error.Code}]: {parsed.Error.Message}", true));
    }

    Result<ExecProgram> program = ExecProgram.Create(parsed.Value.Program, _options);
    if (!program.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"exec error [{program.Error.Code}]: {program.Error.Message}", true));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!budget.IsSuccess)
    {
      return Task.FromResult(new ToolResult($"Error [{budget.Error.Code}]: {budget.Error.Message}", true));
    }

    ExecProgram exec = program.Value with { Budget = budget.Value.Timeout };
    string title = parsed.Value.Title;
    return ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        RunAsync(exec, title, token), ct);
  }

  private async Task<ToolResult> RunAsync(ExecProgram exec, string title, CancellationToken ct)
  {
    Result<IReadOnlyList<ExecParseError>> parse = await _engine.ValidateAsync(exec, ct).ConfigureAwait(false);
    if (!parse.IsSuccess)
    {
      return new ToolResult($"Error [{parse.Error.Code}]: {parse.Error.Message}", true);
    }

    if (parse.Value.Count > 0)
    {
      IReadOnlyList<string> hints = ExecParseHints.Analyze(exec.Text);
      return ExecResultFormatter.ParseErrors(parse.Value, hints);
    }

    long started = Stopwatch.GetTimestamp();
    ExecRunResult run = await _engine.ExecuteAsync(exec, ct).ConfigureAwait(false);

    ToolResult result = ExecResultFormatter.Format(run, title, exec);
    await _activity.RecordAsync(new ExecActivity(
        exec.Text.Length > 80 ? exec.Text[..80] : exec.Text,
        run.Status,
        run.Output.Length,
        Stopwatch.GetElapsedTime(started),
        result.IsError), ct).ConfigureAwait(false);
    return result;
  }
}
