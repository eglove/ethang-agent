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
  public static Result<SkillManageInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(
        ["action", "name", "description", "body", "provenanceSession", "confirm", ToolTimeout.ParameterName],
        StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"Allowed: action, name, description, body, provenanceSession, confirm, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return MissingAction();
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    SkillManageAction? action = actionText switch
    {
      nameof(SkillManageAction.Create) => SkillManageAction.Create,
      nameof(SkillManageAction.Update) => SkillManageAction.Update,
      nameof(SkillManageAction.Delete) => SkillManageAction.Delete,
      _ => null,
    };
    if (action is null)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'action' must be exactly one of Create, Update, Delete (case-sensitive), but got '{actionText}'."));
    }

    if (!json.TryGetProperty("name", out JsonElement nameEl))
    {
      return MissingName();
    }

    if (nameEl.ValueKind != JsonValueKind.String)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'name' must be a string, but got {nameEl.ValueKind}."));
    }

    string name = nameEl.GetString()!;
    if (name.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'name' must be a non-empty string."));
    }

    if (!SkillSpecifications.ValidName.IsMatch(name))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'name' must be a valid skill name: lowercase letters, digits, and hyphens only; " +
          "it must start with a letter or digit and be at most 64 characters long."));
    }

    // description / body: required non-empty for Create, optional-but-non-empty
    // for Update. Type and emptiness are checked here so both actions share one rule.
    string? description = null, body = null;
    if (json.TryGetProperty("description", out JsonElement descEl))
    {
      if (descEl.ValueKind != JsonValueKind.String)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'description' must be a string, but got {descEl.ValueKind}."));
      }

      description = descEl.GetString()!;
    }
    if (json.TryGetProperty("body", out JsonElement bodyEl))
    {
      if (bodyEl.ValueKind != JsonValueKind.String)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'body' must be a string, but got {bodyEl.ValueKind}."));
      }

      body = bodyEl.GetString()!;
    }

    string? provenanceSession = null;
    if (json.TryGetProperty("provenanceSession", out JsonElement provEl))
    {
      if (provEl.ValueKind != JsonValueKind.String)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'provenanceSession' must be a string, but got {provEl.ValueKind}."));
      }

      provenanceSession = provEl.GetString()!;
    }

    switch (action)
    {
      case SkillManageAction.Create:
        if (description is null)
        {
          return Fail(new DomainError(ToolErrorCodes.MissingParameter,
                      "Missing required parameter 'description'. Create requires description and body."));
        }

        if (description.Length == 0)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "'description' must be a non-empty string."));
        }

        if (body is null)
        {
          return Fail(new DomainError(ToolErrorCodes.MissingParameter,
                      "Missing required parameter 'body'. Create requires description and body."));
        }

        if (body.Length == 0)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "'body' must be a non-empty string."));
        }

        break;

      case SkillManageAction.Update:
        if (description is not null && description.Length == 0)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "'description' must be a non-empty string."));
        }

        if (body is not null && body.Length == 0)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "'body' must be a non-empty string."));
        }

        if (description is null && body is null)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "Update changes nothing: provide at least one of 'description' or 'body'."));
        }

        break;

      case SkillManageAction.Delete:
        // Deletion is permanent; anything other than an exact boolean true
        // (including a missing confirm) stops at the gate.
        if (!json.TryGetProperty("confirm", out JsonElement confirmEl) ||
            confirmEl.ValueKind != JsonValueKind.True)
        {
          return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
                      "'confirm' is required to delete a skill and must be exactly the boolean " +
                      "true. Deletion permanently removes current and history rows; re-issue " +
                      "with \"confirm\": true to proceed."));
        }

        break;
      default:
        break;
    }

    return Result.Success<SkillManageInput>(
        new(action.Value, name, description, body, provenanceSession));
  }

  private static Result<SkillManageInput> MissingAction() =>
      Result.Failure<SkillManageInput>(new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'action'. This tool requires action " +
          "(Create, Update, or Delete) and name."));

  private static Result<SkillManageInput> MissingName() =>
      Result.Failure<SkillManageInput>(new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'name'. This tool requires action " +
          "(Create, Update, or Delete) and name."));

  private static Result<SkillManageInput> Fail(DomainError err) =>
      Result.Failure<SkillManageInput>(err);
}
