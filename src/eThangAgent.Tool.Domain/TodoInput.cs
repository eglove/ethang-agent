using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public enum TodoAction { Add, Update, Complete, Remove, List, Clear }

public sealed record TodoInput(TodoAction Action, int? Id, string? Description, TodoStatus? Status)
{
  private const string ActionName = "action";
  private const string IdName = "id";
  private const string DescriptionName = "description";
  private const string StatusName = "status";
  private const string ConfirmName = "confirm";

  private static readonly string[] AllowedNames =
      [ActionName, IdName, DescriptionName, StatusName, ConfirmName, ToolTimeout.ParameterName];

  private static readonly string[] AllowedActions =
      [nameof(TodoAction.Add), nameof(TodoAction.Update), nameof(TodoAction.Complete),
        nameof(TodoAction.Remove), nameof(TodoAction.List), nameof(TodoAction.Clear)];

  private static readonly string[] AllowedStatuses =
      [nameof(TodoStatus.Pending), nameof(TodoStatus.InProgress), nameof(TodoStatus.Completed)];

  public static Result<TodoInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<(TodoAction Action, int? Id, string? Description, TodoStatus? Status)> parsed = ParseFields(json);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error!);
    }

    DomainError? invalid = ValidateForAction(
        parsed.Value.Action, json, parsed.Value.Id, parsed.Value.Description, parsed.Value.Status);
    Result<TodoInput> result = invalid is null
      ? Result.Success<TodoInput>(new(parsed.Value.Action, parsed.Value.Id, parsed.Value.Description, parsed.Value.Status))
      : Result.Failure<TodoInput>(invalid);
    return result;
  }

  /// <summary>Field-level parsing, in declaration order: action, id, description, status.
  ///     Types are exact and enum values exact-match; nothing is coerced.</summary>
  private static Result<(TodoAction Action, int? Id, string? Description, TodoStatus? Status)> ParseFields(
      JsonElement json)
  {
    Result<TodoAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Result.Failure<(TodoAction, int?, string?, TodoStatus?)>(action.Error!);
    }

    Result<int?> id = ParseId(json);
    if (!id.IsSuccess)
    {
      return Result.Failure<(TodoAction, int?, string?, TodoStatus?)>(id.Error!);
    }

    Result<string?> description = ToolArguments.OptionalString(json, DescriptionName);
    if (!description.IsSuccess)
    {
      return Result.Failure<(TodoAction, int?, string?, TodoStatus?)>(description.Error!);
    }

    Result<TodoStatus?> status = ParseStatus(json);
    Result<(TodoAction Action, int? Id, string? Description, TodoStatus? Status)> fields = status.IsSuccess
      ? Result.Success((action.Value, id.Value, description.Value, status.Value))
      : Result.Failure<(TodoAction, int?, string?, TodoStatus?)>(status.Error!);
    return fields;
  }

  private static Result<TodoAction> ParseAction(JsonElement json)
  {
    Result<string> text = ToolArguments.RequireString(json, ActionName,
        "This tool requires action (Add, Update, Complete, Remove, List, or Clear).");
    return text.IsSuccess
      ? ToolArguments.ParseEnum<TodoAction>(ActionName, text.Value!, AllowedActions)
      : Result.Failure<TodoAction>(text.Error!);
  }

  private static Result<int?> ParseId(JsonElement json)
  {
    if (!json.TryGetProperty(IdName, out JsonElement idEl))
    {
      return Result.Success<int?>(null);
    }

    if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt32(out int parsedId))
    {
      return Result.Failure<int?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{IdName}' must be an integer, but got {idEl.ValueKind}."));
    }

    Result<int?> id = parsedId > 0
      ? Result.Success<int?>(parsedId)
      : Result.Failure<int?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{IdName}' must be a positive integer, but got {parsedId}."));
    return id;
  }

  private static Result<TodoStatus?> ParseStatus(JsonElement json)
  {
    Result<string?> text = ToolArguments.OptionalString(json, StatusName);
    if (!text.IsSuccess)
    {
      return Result.Failure<TodoStatus?>(text.Error!);
    }

    if (text.Value is not { } statusText)
    {
      return Result.Success<TodoStatus?>(null);
    }

    Result<TodoStatus> status = ToolArguments.ParseEnum<TodoStatus>(StatusName, statusText, AllowedStatuses);
    Result<TodoStatus?> result = status.IsSuccess
      ? Result.Success<TodoStatus?>(status.Value)
      : Result.Failure<TodoStatus?>(status.Error!);
    return result;
  }

  /// <summary>Action-level rules, checked in the tool's documented order after every
  ///     field parses. Returns the violation, or null when the call is well-formed.</summary>
  private static DomainError? ValidateForAction(TodoAction action, JsonElement json,
      int? id, string? description, TodoStatus? status)
  {
    if (action == TodoAction.Add)
    {
      return ValidateAdd(description, status);
    }

    if (action == TodoAction.Update)
    {
      return ValidateUpdate(id, description, status);
    }

    if (action is TodoAction.Complete or TodoAction.Remove)
    {
      return RequireTargetId(id);
    }

    if (action == TodoAction.Clear)
    {
      return RequireClearConfirmation(json);
    }

    return null; // List: no extra rules.
  }

  private static DomainError? ValidateAdd(string? description, TodoStatus? status)
  {
    // 'status' is meaningful elsewhere in this tool, so an explicit one on
    // Add is rejected, not silently dropped: items always start Pending.
    if (status is not null)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'status' is not accepted for Add: items always start Pending. " +
                  "Use Update to change an item's status.");
    }

    if (description is null)
    {
      return new DomainError(ToolErrorCodes.MissingParameter,
                  "Missing required parameter 'description'. Add requires a non-empty description.");
    }

    DomainError? empty = description.Length == 0
      ? new DomainError(ToolErrorCodes.InvalidParameterValue, "'description' must be a non-empty string.")
      : null;
    return empty;
  }

  private static DomainError? ValidateUpdate(int? id, string? description, TodoStatus? status)
  {
    if (id is null)
    {
      return MissingId();
    }

    if (description is not null && description.Length == 0)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'description' must be a non-empty string.");
    }

    DomainError? nothingToUpdate = description is null && status is null
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Update changes nothing: provide at least one of 'description' or 'status'.")
      : null;
    return nothingToUpdate;
  }

  private static DomainError? RequireTargetId(int? id)
  {
    DomainError? missing = id is null ? MissingId() : null;
    return missing;
  }

  private static DomainError? RequireClearConfirmation(JsonElement json)
  {
    // Clearing is destructive; anything other than an exact boolean true
    // (including a missing confirm) stops at the gate.
    DomainError? unconfirmed = !json.TryGetProperty(ConfirmName, out JsonElement confirmEl) ||
        confirmEl.ValueKind != JsonValueKind.True
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'confirm' is required to clear the todo list and must be exactly the " +
                  "boolean true. Re-issue with \"confirm\": true to proceed.")
      : null;
    return unconfirmed;
  }

  private static DomainError MissingId() =>
      new(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'id'. Update, Complete, and Remove require the id of the target item.");

  private static Result<TodoInput> Fail(DomainError err) =>
      Result.Failure<TodoInput>(err);
}
