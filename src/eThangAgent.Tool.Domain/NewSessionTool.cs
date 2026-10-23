using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Starts a NEW session in this workspace (issue #234 task 1): the
///     current conversation ends (it stays resumable via the Sessions dialog)
///     and the new session opens in the same tab with a fresh context. Strict
///     argument validation, exactly one port call per invocation, and a
///     one-line confirmation carrying the port's session id verbatim. Port
///     failures surface verbatim as tool errors.</summary>
public sealed class NewSessionTool(INewSessionPort port) : ITool
{
  private readonly INewSessionPort _port = port ?? throw new ArgumentNullException(nameof(port));

  public ToolDefinition Definition { get; } = new(
      "new_session",
      "Start a NEW session in this workspace. The current conversation ends (it stays resumable via the Sessions dialog); " +
      "the new session opens in the same tab with a fresh context. timeoutSeconds and prompt are mandatory. " +
      "Output: one confirmation line '[new session <id> started; the conversation continues there.]'. " +
      "Errors begin with `Error [Code]:` — InvalidInput, SessionLaunchFailed, TurnBusy. " +
      "A spawned child may not call this tool (GrantViolation when denied).",
      [
        new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
        new ToolParameter("prompt", ToolParameterType.Text, "The first prompt for the new session."),
      ],
      ["timeoutSeconds", "prompt"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return budget.IsSuccess
        ? ToolExecution.RunAsync(input.Name, budget.Value.Timeout, CoreAsync(input), ct)
        : Task.FromResult(Fail(budget.Error));
  }

  private Func<CancellationToken, Task<ToolResult>> CoreAsync(RawToolInput input) => async token =>
  {
    Result<JsonElement> args = ToolArguments.ParseObject(input.JsonArguments);
    if (!args.IsSuccess)
    {
      return Fail(args.Error);
    }

    Result<string> prompt = NewSessionInput.Parse(args.Value);
    if (!prompt.IsSuccess)
    {
      return Fail(prompt.Error);
    }

    Result<NewSessionPortResult> started = await _port.StartAsync(prompt.Value, handoffSummary: null, token).ConfigureAwait(false);
    return !started.IsSuccess
        ? Fail(started.Error)
        : Ok($"[new session {started.Value.SessionId} started; the conversation continues there.]");
  };

  private static ToolResult Ok(string content) => new(content, IsError: false);

  private static ToolResult Fail(DomainError error) => new($"Error [{error.Code}]: {error.Message}", IsError: true);
}

/// <summary>Strict input parsing for new_session (issue #234 task 1):
///     exactly one required string parameter, prompt, plus the mandatory
///     timeoutSeconds budget key — unknown keys are rejected by name.</summary>
internal static class NewSessionInput
{
  public static Result<string> Parse(JsonElement args)
  {
    DomainError? unknown = ToolArguments.RejectUnknownParameters(args, "timeoutSeconds", "prompt");
    if (unknown is not null)
    {
      return Result.Failure<string>(unknown);
    }

    if (!args.TryGetProperty("prompt", out JsonElement el))
    {
      return ToolArguments.Missing<string>("prompt", "The first prompt for the new session.");
    }

    Result<string> text = el.ValueKind == JsonValueKind.String
        ? Result.Success(el.GetString()!)
        : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'prompt' must be a string, but got {el.ValueKind}."));
    if (!text.IsSuccess)
    {
      return text;
    }

    string trimmed = text.Value.Trim();
    return trimmed.Length == 0
        ? Result.Failure<string>(new DomainError("InvalidInput", "prompt must be a non-empty string."))
        : Result.Success(trimmed);
  }
}
