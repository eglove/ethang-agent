using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Reads the stored output of a user command run (the ! chat command).
///     Without an id, returns the latest run in this workspace; with one, that run.
///     Output format contract: an annotation line
///     `[command-run {id}] command: {cmd} | exit code {n}` (or `| TIMED OUT (partial
///     output)` when the budget expired), a truncation notice when tailLines capped
///     the result, then the captured output lines. Errors begin with `Error [Code]:`.
///     Runs persist across sessions; the ids a resumed conversation sees in its
///     system messages resolve here.</summary>
public sealed class CommandOutputTool(ICommandRunStore store) : ITool
{
  private const int DefaultTailLines = 400;

  private readonly ICommandRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

  public ToolDefinition Definition { get; } = new(
      "command_output",
      "Read the stored output of a user command run (a ! command the user ran in chat). " +
      "Call it when the user references a command's result. Without arguments, returns " +
      "the MOST RECENT run; pass {\"id\": N} for a specific run (ids appear in the " +
      "conversation's system messages as 'User ran: ... (id: N, ...)'). Optionally pass " +
      "{\"tailLines\": N} to cap output to its last N lines. Output begins with an " +
      "annotation line `[command-run <id>] command: <cmd> | exit code <n>` (or " +
      "`| TIMED OUT (partial output)`); a truncation notice precedes capped tails. " +
      "Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("id", ToolParameterType.WholeNumber, "The run id to read; omit for the most recent run.", Minimum: 1),
          new ToolParameter("tailLines", ToolParameterType.WholeNumber, "Cap output to the last N lines; omit for the default cap (400).", Minimum: 1),
      ],
      ["timeoutSeconds"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<CommandOutputArgs> args = ParseArguments(input.JsonArguments);
    if (!args.IsSuccess)
    {
      return Task.FromResult(Err(args.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => ReadAsync(args.Value, token), ct);
  }

  private async Task<ToolResult> ReadAsync(CommandOutputArgs args, CancellationToken ct)
  {
    Result<CommandRun> resolved = args.Id is { } id
        ? await _store.GetAsync(id, ct).ConfigureAwait(false)
        : await _store.GetLatestAsync(ct).ConfigureAwait(false);
    if (!resolved.IsSuccess)
    {
      return Err(resolved.Error);
    }

    CommandRun run = resolved.Value;
    string status = run.TimedOut ? "TIMED OUT (partial output)" : $"exit code {run.ExitCode}";
    List<string> lines =
    [
        $"[command-run {run.Id}] command: {run.Command} | {status}"
    ];

    string[] outputLines = run.Output.Length == 0
        ? []
        : run.Output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    if (outputLines.Length > args.TailLines)
    {
      lines.Add($"[showing last {args.TailLines} of {outputLines.Length} lines — pass tailLines to see more]");
      outputLines = outputLines[^args.TailLines..];
    }
    else if (outputLines.Length == 0)
    {
      lines.Add("(no output)");
    }

    lines.AddRange(outputLines);
    return new ToolResult(string.Join("\n", lines), false);
  }

  private static Result<CommandOutputArgs> ParseArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<CommandOutputArgs>(baseParse.Error);
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(baseParse.Value);
    if (!budget.IsSuccess)
    {
      return Result.Failure<CommandOutputArgs>(budget.Error);
    }

    int? id = null;
    int tailLines = DefaultTailLines;
    List<string> unknown = [];
    foreach (JsonProperty property in baseParse.Value.EnumerateObject())
    {
      switch (property.Name)
      {
        case "id":
          if (property.Value.ValueKind is not JsonValueKind.Number ||
              !property.Value.TryGetInt32(out int parsedId) || parsedId < 1)
          {
            return Result.Failure<CommandOutputArgs>(new DomainError("InvalidParameterValue",
                "'id' must be a positive whole number."));
          }

          id = parsedId;
          break;
        case "tailLines":
          if (property.Value.ValueKind is not JsonValueKind.Number ||
              !property.Value.TryGetInt32(out int parsedTail) || parsedTail < 1)
          {
            return Result.Failure<CommandOutputArgs>(new DomainError("InvalidParameterValue",
                "'tailLines' must be a positive whole number."));
          }

          tailLines = parsedTail;
          break;
        case ToolTimeout.ParameterName:
          break;
        default:
          unknown.Add(property.Name);
          break;
      }
    }

    return unknown.Count > 0
      ? Result.Failure<CommandOutputArgs>(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          "Supported: timeoutSeconds, id, tailLines."))
      : Result.Success(new CommandOutputArgs(id, tailLines));
  }

  private sealed record CommandOutputArgs(int? Id, int TailLines);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
