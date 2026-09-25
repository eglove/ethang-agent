using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strict input parsing for the registry tools (plan #29 task 9).</summary>
internal static class SkillRegistryInputs
{
  public static Result<string> ParseQuery(JsonElement args)
  {
    if (!args.TryGetProperty("query", out JsonElement q) || q.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError("InvalidInput", "query is required and must be a string."));
    }

    string value = q.GetString()!.Trim();
    return value.Length is >= 1 and <= 200
        ? Result.Success(value)
        : Result.Failure<string>(new DomainError("InvalidInput", "query must be 1-200 characters."));
  }

  public static Result<SkillRegistryAction> ParseAction(JsonElement args)
  {
    return !args.TryGetProperty("action", out JsonElement a) || a.ValueKind != JsonValueKind.String
        ? Result.Failure<SkillRegistryAction>(new DomainError("InvalidInput", "action is required (Install|Update|Uninstall)."))
        : a.GetString() switch
        {
          "Install" => Result.Success(SkillRegistryAction.Install),
          "Update" => Result.Success(SkillRegistryAction.Update),
          "Uninstall" => Result.Success(SkillRegistryAction.Uninstall),
          _ => Result.Failure<SkillRegistryAction>(new DomainError("InvalidInput", "action must be exactly Install, Update, or Uninstall (case-sensitive).")),
        };
  }

  public static Result<string?> ParseAddress(JsonElement args, bool required)
  {
    if (!args.TryGetProperty("address", out JsonElement a))
    {
      return required
          ? Result.Failure<string?>(new DomainError("InvalidInput", "address is required for this action."))
          : Result.Success<string?>(null);
    }

    string value = a.ValueKind == JsonValueKind.String ? a.GetString()!.Trim() : string.Empty;
    return value.Length > 0
        ? Result.Success<string?>(value)
        : Result.Failure<string?>(new DomainError("InvalidInput", "address must be a non-empty string."));
  }

  public static Result<string?> ParseName(JsonElement args, bool required)
  {
    if (!args.TryGetProperty("name", out JsonElement n))
    {
      return required
          ? Result.Failure<string?>(new DomainError("InvalidInput", "name is required for this action."))
          : Result.Success<string?>(null);
    }

    string value = n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : string.Empty;
    return value.Length > 0
        ? Result.Success<string?>(value)
        : Result.Failure<string?>(new DomainError("InvalidInput", "name must be a non-empty string."));
  }

  public static Result<string?> ParseTarget(JsonElement args, bool required)
  {
    if (!args.TryGetProperty("target", out JsonElement t))
    {
      return required
          ? Result.Failure<string?>(new DomainError("InvalidInput", "target is required for this action."))
          : Result.Success<string?>(null);
    }

    string value = t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : string.Empty;
    return value is "global" or "workspace"
        ? Result.Success<string?>(value)
        : Result.Failure<string?>(new DomainError("InvalidInput", "target must be exactly 'global' or 'workspace'."));
  }

  public static bool ParseFlag(JsonElement args, string name) =>
      args.TryGetProperty(name, out JsonElement f) && f.ValueKind == JsonValueKind.True;
}

internal enum SkillRegistryAction
{
  Install,
  Update,
  Uninstall,
}