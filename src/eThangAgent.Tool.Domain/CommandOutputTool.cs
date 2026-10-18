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

    ParsedProperties properties = ParseProperties(baseParse.Value);
    return properties.Unknown.Count > 0
      ? Result.Failure<CommandOutputArgs>(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", properties.Unknown)}. " +
          "Supported: timeoutSeconds, id, tailLines."))
      : Result.Success(new CommandOutputArgs(properties.Id, properties.TailLines));
  }

  private static ParsedProperties ParseProperties(JsonElement json)
  {
    int? id = null;
    int tailLines = DefaultTailLines;
    List<string> unknown = [];
    foreach (JsonProperty property in json.EnumerateObject())
    {
      switch (property.Name)
      {
        case "id":
          Result<int> parsedId = RequirePositive(property.Value, "'id' must be a positive whole number.");
          if (!parsedId.IsSuccess)
          {
            return ParsedProperties.Failure(parsedId.Error, unknown);
          }

          id = parsedId.Value;
          break;
        case "tailLines":
          Result<int> parsedTail = RequirePositive(property.Value, "'tailLines' must be a positive whole number.");
          if (!parsedTail.IsSuccess)
          {
            return ParsedProperties.Failure(parsedTail.Error, unknown);
          }

          tailLines = parsedTail.Value;
          break;
        case ToolTimeout.ParameterName:
          break;
        default:
          unknown.Add(property.Name);
          break;
      }
    }

    return new ParsedProperties(id, tailLines, unknown);
  }

  private static Result<int> RequirePositive(JsonElement value, string message) =>
      value.ValueKind is not JsonValueKind.Number || !value.TryGetInt32(out int parsed) || parsed < 1
        ? Result.Failure<int>(new DomainError("InvalidParameterValue", message))
        : Result.Success(parsed);

  private sealed record ParsedProperties(int? Id, int TailLines, List<string> Unknown)
  {
    public static ParsedProperties Failure(DomainError error, List<string> unknownSoFar) =>
        new(null, DefaultTailLines, [.. unknownSoFar, error.Message]);
  }

  private sealed record CommandOutputArgs(int? Id, int TailLines);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
