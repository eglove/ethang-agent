using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain;

public enum SkillManageAction { Create, Update, Delete }

public sealed record SkillManageInput(
    SkillManageAction Action,
    string Name,
    string? Description,
    string? Body,
    string? ProvenanceSession)
{
  private const string ActionName = "action";
  private const string NameName = "name";
  private const string DescriptionName = "description";
  private const string BodyName = "body";
  private const string ProvenanceSessionName = "provenanceSession";
  private const string ConfirmName = "confirm";
  private const string ActionAndNameRequirement = "This tool requires action (Create, Update, or Delete) and name.";

  private static readonly string[] AllowedNames =
      [ActionName, NameName, DescriptionName, BodyName, ProvenanceSessionName, ConfirmName, ToolTimeout.ParameterName];

  private static readonly string[] AllowedActions =
      [nameof(SkillManageAction.Create), nameof(SkillManageAction.Update), nameof(SkillManageAction.Delete)];

  public static Result<SkillManageInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)>
        parsed = ParseFields(json);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error);
    }

    DomainError? invalid = ValidateForAction(parsed.Value.Action, json, parsed.Value.Description, parsed.Value.Body);
    Result<SkillManageInput> result = invalid is null
      ? Result.Success<SkillManageInput>(
          new(parsed.Value.Action, parsed.Value.Name, parsed.Value.Description, parsed.Value.Body, parsed.Value.ProvenanceSession))
      : Result.Failure<SkillManageInput>(invalid);
    return result;
  }

  /// <summary>Field-level parsing, in declaration order: action, name, description,
  ///     body, provenanceSession. Types are exact and enum values exact-match.</summary>
  private static Result<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)>
      ParseFields(JsonElement json)
  {
    Result<SkillManageAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return FailFields(action.Error);
    }

    Result<string> name = ParseName(json);
    if (!name.IsSuccess)
    {
      return FailFields(name.Error);
    }

    // description / body: required non-empty for Create, optional-but-non-empty
    // for Update. Type is checked here so both actions share one rule.
    Result<string?> description = ToolArguments.OptionalString(json, DescriptionName);
    if (!description.IsSuccess)
    {
      return FailFields(description.Error);
    }

    Result<string?> body = ToolArguments.OptionalString(json, BodyName);
    if (!body.IsSuccess)
    {
      return FailFields(body.Error);
    }

    Result<string?> provenanceSession = ToolArguments.OptionalString(json, ProvenanceSessionName);
    Result<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)> fields =
        provenanceSession.IsSuccess
      ? Result.Success<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)>(
          (action.Value, name.Value, description.Value, body.Value, provenanceSession.Value))
      : FailFields(provenanceSession.Error);
    return fields;
  }

  private static Result<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)>
      FailFields(DomainError error)
      => Result.Failure<(SkillManageAction Action, string Name, string? Description, string? Body, string? ProvenanceSession)>(error);

  private static Result<SkillManageAction> ParseAction(JsonElement json)
  {
    Result<string> text = ToolArguments.RequireString(json, ActionName, ActionAndNameRequirement);
    return text.IsSuccess
      ? ToolArguments.ParseEnum<SkillManageAction>(ActionName, text.Value, AllowedActions)
      : Result.Failure<SkillManageAction>(text.Error);
  }

  private static Result<string> ParseName(JsonElement json)
  {
    Result<string> name = ToolArguments.RequireString(json, NameName, ActionAndNameRequirement);
    if (!name.IsSuccess)
    {
      return name;
    }

    string value = name.Value;
    if (value.Length == 0)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'name' must be a non-empty string."));
    }

    Result<string> valid = SkillSpecifications.ValidName.IsMatch(value)
      ? Result.Success(value)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'name' must be a valid skill name: lowercase letters, digits, and hyphens only; " +
          "it must start with a letter or digit and be at most 64 characters long."));
    return valid;
  }

  /// <summary>Action-level rules, checked in the tool's documented order after every
  ///     field parses. Returns the violation, or null when the call is well-formed.</summary>
  private static DomainError? ValidateForAction(SkillManageAction action, JsonElement json,
      string? description, string? body)
  {
    if (action == SkillManageAction.Create)
    {
      return ValidateCreate(description, body);
    }

    if (action == SkillManageAction.Update)
    {
      return ValidateUpdate(description, body);
    }

    return RequireDeleteConfirmation(json); // Delete
  }

  private static DomainError? ValidateCreate(string? description, string? body)
  {
    if (description is null)
    {
      return new DomainError(ToolErrorCodes.MissingParameter,
                  "Missing required parameter 'description'. Create requires description and body.");
    }

    if (description.Length == 0)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'description' must be a non-empty string.");
    }

    if (body is null)
    {
      return new DomainError(ToolErrorCodes.MissingParameter,
                  "Missing required parameter 'body'. Create requires description and body.");
    }

    DomainError? emptyBody = body.Length == 0
      ? new DomainError(ToolErrorCodes.InvalidParameterValue, "'body' must be a non-empty string.")
      : null;
    return emptyBody;
  }

  private static DomainError? ValidateUpdate(string? description, string? body)
  {
    if (description is not null && description.Length == 0)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'description' must be a non-empty string.");
    }

    if (body is not null && body.Length == 0)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'body' must be a non-empty string.");
    }

    DomainError? nothingToUpdate = description is null && body is null
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "Update changes nothing: provide at least one of 'description' or 'body'.")
      : null;
    return nothingToUpdate;
  }

  private static DomainError? RequireDeleteConfirmation(JsonElement json)
  {
    // Deletion is permanent; anything other than an exact boolean true
    // (including a missing confirm) stops at the gate.
    DomainError? unconfirmed = !json.TryGetProperty(ConfirmName, out JsonElement confirmEl) ||
        confirmEl.ValueKind != JsonValueKind.True
      ? new DomainError(ToolErrorCodes.InvalidParameterValue,
                  "'confirm' is required to delete a skill and must be exactly the boolean " +
                  "true. Deletion permanently removes current and history rows; re-issue " +
                  "with \"confirm\": true to proceed.")
      : null;
    return unconfirmed;
  }

  private static Result<SkillManageInput> Fail(DomainError err) =>
      Result.Failure<SkillManageInput>(err);
}
