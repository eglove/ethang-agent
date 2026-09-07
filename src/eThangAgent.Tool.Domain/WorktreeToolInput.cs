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

    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Fail(new DomainError("InvalidAction",
          "'action' is required: exactly one of 'list', 'create', or 'remove' (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType, $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    Result<WorktreeAction> action = actionText switch
    {
      "list" => Result.Success(WorktreeAction.List),
      "create" => Result.Success(WorktreeAction.Create),
      "remove" => Result.Success(WorktreeAction.Remove),
      _ => Result.Failure<WorktreeAction>(new DomainError("InvalidAction", $"'action' must be exactly one of 'list', 'create', or 'remove' (case-sensitive; got '{actionText}').")),
    };
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    string[] allowed = action.Value switch
    {
      WorktreeAction.List => ["action", ToolTimeout.ParameterName],
      WorktreeAction.Create => ["action", "name", ToolTimeout.ParameterName],
      WorktreeAction.Remove => ["action", "name", "force", ToolTimeout.ParameterName],
      _ => throw new InvalidOperationException("unreachable: action is a validated enum value"),
    };
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, allowed);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    string? name = null;
    if (json.TryGetProperty("name", out _))
    {
      Result<string?> readName = ToolArguments.OptionalString(json, "name");
      if (!readName.IsSuccess)
      {
        return Fail(readName.Error);
      }

      name = readName.Value;
    }

    if (action.Value is not WorktreeAction.List && name is null)
    {
      return ToolArguments.Missing<WorktreeToolInput>("name", $"The '{actionText}' action requires name: 1..64 characters of lowercase letters, digits, and hyphens (^[a-z0-9-]+$).");
    }

    bool? force = null;
    if (json.TryGetProperty("force", out _))
    {
      Result<bool?> readForce = ToolArguments.OptionalBool(json, "force");
      if (!readForce.IsSuccess)
      {
        return Fail(readForce.Error);
      }

      force = readForce.Value;
    }

    return Result.Success(new WorktreeToolInput(action.Value, name, force));
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
