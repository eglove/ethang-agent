using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record EditToolInput(string Path, string Old, string New, bool All, int Occurrences)
{
  public static Result<EditToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["path", "old", "new", "all", "occurrences", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, old, new, all, occurrences, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return Missing("path");
    }

    if (pathEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("path", "string", pathEl.ValueKind);
    }

    string path = pathEl.GetString()!;
    if (path.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be a non-empty string."));
    }

    if (!json.TryGetProperty("old", out JsonElement oldEl))
    {
      return Missing("old");
    }

    if (oldEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("old", "string", oldEl.ValueKind);
    }

    string old = oldEl.GetString()!;
    if (old.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'old' must be a non-empty string — an empty anchor would match everywhere."));
    }

    if (!json.TryGetProperty("new", out JsonElement newEl))
    {
      return Missing("new");
    }

    if (newEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("new", "string", newEl.ValueKind);
    }

    string @new = newEl.GetString()!; // may be empty: deletion is explicit intent

    bool hasAll = json.TryGetProperty("all", out JsonElement allEl);
    bool hasOcc = json.TryGetProperty("occurrences", out JsonElement occEl);
    if (hasAll == hasOcc)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Provide exactly one of 'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
    }

    bool all;
    int occurrences;
    if (hasAll)
    {
      if (allEl.ValueKind is not JsonValueKind.True)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'all' must be exactly true. Provide exactly one of " +
            "'all' (boolean true) or 'occurrences' (integer ≥ 1)."));
      }

      all = true;
      occurrences = 0;
    }
    else
    {
      if (occEl.ValueKind != JsonValueKind.Number || !occEl.TryGetInt32(out occurrences))
      {
        return WrongType("occurrences", "integer", occEl.ValueKind);
      }

      if (occurrences < 1)
      {
        return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'occurrences' must be ≥ 1 (got {occurrences})."));
      }

      all = false;
    }

    return Result.Success<EditToolInput>(new(path, old, @new, all, occurrences));
  }

  private static Result<EditToolInput> Missing(string n) =>
      Result.Failure<EditToolInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires path, old, and new, plus exactly one of 'all' or 'occurrences'."));

  private static Result<EditToolInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<EditToolInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<EditToolInput> Fail(DomainError err) =>
      Result.Failure<EditToolInput>(err);
}
