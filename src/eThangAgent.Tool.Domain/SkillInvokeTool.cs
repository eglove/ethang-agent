using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Invokes a skill on the user's behalf (spec #28 task 2): resolves
///     through the invocation port, appends the System message out-of-turn
///     (the SkillDirectoryReloader precedent: conversation plus best-effort
///     notice sink), and returns a one-line confirmation — the body itself is
///     NOT repeated in the tool result.</summary>
public sealed class SkillInvokeTool(ISkillInvocationPort invocation, IConversationSink? conversation, Action<string>? noticeSink) : ITool
{
  private readonly ISkillInvocationPort _invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));

  public ToolDefinition Definition { get; } = new(
      "skill_invoke",
      "Load and follow a named skill on the user's behalf — including manual skills that never appear in the " +
      "listing. timeoutSeconds and name are mandatory; args is optional free text carried to the model as the " +
      "invocation's arguments. Output: one confirmation line '[skill invoked: <name>] (args: <args>)' or " +
      "'[skill invoked: <name>] (no args)'; the skill's content enters the conversation as a System message. " +
      "Errors begin with `Error [Code]:` — SkillNotFound, AmbiguousSkill.",
      [
        new ToolParameter("name", ToolParameterType.Text, "The skill's catalog name."),
        new ToolParameter("args", ToolParameterType.Text, "Optional free-text arguments for the invocation."),
      ],
      ["timeoutSeconds", "name"]);

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
    System.Text.Json.JsonElement args = System.Text.Json.JsonDocument.Parse(input.JsonArguments).RootElement;
    Result<(string Name, string? Args)> parsed = SkillInvokeInput.Parse(args);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error);
    }

    Result<SkillInvocationPortResult> r = await _invocation.InvokeAsync(parsed.Value.Name, parsed.Value.Args, token).ConfigureAwait(false);
    if (!r.IsSuccess)
    {
      return Fail(r.Error);
    }

    conversation?.AddSystemMessage(r.Value.SystemLine);

    // CA1031: the catch-all IS the named best-effort decision (spec #28 task 2) -
    // a broken transcript notice may never fail the tool.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      noticeSink?.Invoke(r.Value.SystemLine);
    }
    catch
    {
      // Best-effort (named decision): the transcript notice never fails the tool.
    }
#pragma warning restore CA1031 // Do not catch general exception types

    string confirmation = parsed.Value.Args is null
        ? $"[skill invoked: {r.Value.Name}] (no args)"
        : $"[skill invoked: {r.Value.Name}] (args: {parsed.Value.Args})";
    return Ok(confirmation);
  };

  private static ToolResult Ok(string content) => new(content, IsError: false);

  private static ToolResult Fail(DomainError error) => new($"Error [{error.Code}]: {error.Message}", IsError: true);
}

/// <summary>Strict input parsing for skill_invoke (spec #28 task 2).</summary>
internal static class SkillInvokeInput
{
  public static Result<(string Name, string? Args)> Parse(System.Text.Json.JsonElement args)
  {
    if (!args.TryGetProperty("name", out System.Text.Json.JsonElement n) || n.ValueKind != System.Text.Json.JsonValueKind.String)
    {
      return Result.Failure<(string, string?)>(new DomainError("InvalidInput", "name is required and must be a non-empty string."));
    }

    string name = n.GetString()!.Trim();
    if (name.Length == 0)
    {
      return Result.Failure<(string, string?)>(new DomainError("InvalidInput", "name must be a non-empty string."));
    }

    if (!args.TryGetProperty("args", out System.Text.Json.JsonElement a))
    {
      return Result.Success((name, (string?)null));
    }

    if (a.ValueKind != System.Text.Json.JsonValueKind.String)
    {
      return Result.Failure<(string, string?)>(new DomainError("InvalidInput", "args must be a string."));
    }

    string argsText = a.GetString()!.Trim();
    return argsText.Length == 0
        ? Result.Success((name, (string?)null))
        : Result.Success((name, (string?)argsText));
  }
}
