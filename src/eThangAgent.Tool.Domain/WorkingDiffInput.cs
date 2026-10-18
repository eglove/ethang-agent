using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WorkingDiffInput(string Scope, string? Path)
{
  public static Result<WorkingDiffInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = RejectUnknown(json);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> scope = ParseScope(json);
    if (!scope.IsSuccess)
    {
      return Result.Failure<WorkingDiffInput>(scope.Error);
    }

    Result<string?> path = ParsePath(json);
    return path.IsSuccess
      ? Result.Success<WorkingDiffInput>(new(scope.Value, path.Value))
      : Result.Failure<WorkingDiffInput>(path.Error);
  }

  private static DomainError? RejectUnknown(JsonElement json)
  {
    HashSet<string> known = new(["scope", "path", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    return unknown.Count > 0
      ? new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: scope, path, {ToolTimeout.ParameterName}.")
      : null;
  }

  private static Result<string> ParseScope(JsonElement json)
  {
    if (!json.TryGetProperty("scope", out JsonElement scopeEl))
    {
      return Missing();
    }

    if (scopeEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(WrongType(scopeEl.ValueKind));
    }

    string scope = scopeEl.GetString()!;
    return scope is "Staged" or "Unstaged" or "All"
      ? Result.Success(scope)
      : Result.Failure<string>(new DomainError("InvalidParameterValue",
          $"'scope' must be exactly \"Staged\", \"Unstaged\", or \"All\" " +
          $"(case-sensitive; got \"{scope}\")."));
  }

  private static Result<string?> ParsePath(JsonElement json)
  {
    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return Result.Success<string?>(null);
    }

    if (pathEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string?>(WrongType(pathEl.ValueKind, "path"));
    }

    string path = pathEl.GetString()!;
    return path.Length > 0
      ? Result.Success<string?>(path)
      : Result.Failure<string?>(new DomainError("InvalidParameterValue",
          "'path' must be a non-empty string when present."));
  }

  private static Result<string> Missing() =>
      Result.Failure<string>(new DomainError("MissingParameter",
          "Missing required parameter 'scope'. This tool requires scope."));

  private static DomainError WrongType(JsonValueKind actual, string name = "scope") =>
      new("InvalidParameterType",
          $"'{name}' must be a string, but got {actual}.");

  private static Result<WorkingDiffInput> Fail(DomainError err) =>
      Result.Failure<WorkingDiffInput>(err);
}
