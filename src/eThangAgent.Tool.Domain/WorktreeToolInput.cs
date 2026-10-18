using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly parsed arguments for the 'worktree' tool: a required action
///     (exactly 'list', 'create', or 'remove', case-sensitive — absent or unknown
///     both fail InvalidAction), a name required for create and remove, and an
///     optional force flag only remove admits.</summary>
public sealed record WorktreeToolInput(WorktreeAction Action, string? Name, bool? Force)
{
  public static Result<WorktreeToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    Result<ParsedAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNamesFor(action.Value.Action));
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string?> name = ParseName(json);
    if (!name.IsSuccess)
    {
      return Fail(name.Error);
    }

    if (action.Value.Action is not WorktreeAction.List && name.Value is null)
    {
      return ToolArguments.Missing<WorktreeToolInput>("name", $"The '{action.Value.Text}' action requires name: 1..64 characters of lowercase letters, digits, and hyphens (^[a-z0-9-]+$).");
    }

    Result<bool?> force = ParseForce(json);
    Result<WorktreeToolInput> created = force.IsSuccess
      ? Result.Success(new WorktreeToolInput(action.Value.Action, name.Value, force.Value))
      : Fail(force.Error);
    return created;
  }

  private sealed record ParsedAction(WorktreeAction Action, string Text);

  private static Result<ParsedAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<ParsedAction>(new DomainError("InvalidAction",
          "'action' is required: exactly one of 'list', 'create', or 'remove' (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<ParsedAction>(new DomainError(ToolErrorCodes.InvalidParameterType, $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    Result<ParsedAction> action = actionText switch
    {
      "list" => Result.Success(new ParsedAction(WorktreeAction.List, actionText)),
      "create" => Result.Success(new ParsedAction(WorktreeAction.Create, actionText)),
      "remove" => Result.Success(new ParsedAction(WorktreeAction.Remove, actionText)),
      _ => Result.Failure<ParsedAction>(new DomainError("InvalidAction", $"'action' must be exactly one of 'list', 'create', or 'remove' (case-sensitive; got '{actionText}').")),
    };
    return action;
  }

  private static string[] AllowedNamesFor(WorktreeAction action) => action switch
  {
    WorktreeAction.List => ["action", ToolTimeout.ParameterName],
    WorktreeAction.Create => ["action", "name", ToolTimeout.ParameterName],
    WorktreeAction.Remove => ["action", "name", "force", ToolTimeout.ParameterName],
    _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
  };

  private static Result<string?> ParseName(JsonElement json)
  {
    if (!json.TryGetProperty("name", out _))
    {
      return Result.Success<string?>(null);
    }

    Result<string?> readName = ToolArguments.OptionalString(json, "name");
    return readName.IsSuccess
      ? Result.Success<string?>(readName.Value)
      : Result.Failure<string?>(readName.Error);
  }

  private static Result<bool?> ParseForce(JsonElement json)
  {
    if (!json.TryGetProperty("force", out _))
    {
      return Result.Success<bool?>(null);
    }

    Result<bool?> readForce = ToolArguments.OptionalBool(json, "force");
    return readForce.IsSuccess
      ? Result.Success<bool?>(readForce.Value)
      : Result.Failure<bool?>(readForce.Error);
  }

  private static Result<WorktreeToolInput> Fail(DomainError error) => Result.Failure<WorktreeToolInput>(error);
}

/// <summary>The admitted 'worktree' actions.</summary>
public enum WorktreeAction
{
  List,
  Create,
  Remove,
}
