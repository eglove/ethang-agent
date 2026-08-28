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

    HashSet<string> known = new(["scope", "path", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: scope, path, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("scope", out JsonElement scopeEl))
    {
      return Missing();
    }

    if (scopeEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(scopeEl.ValueKind);
    }

    string scope = scopeEl.GetString()!;
    if (scope is not ("Staged" or "Unstaged" or "All"))
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'scope' must be exactly \"Staged\", \"Unstaged\", or \"All\" " +
          $"(case-sensitive; got \"{scope}\")."));
    }

    string? path = null;
    if (json.TryGetProperty("path", out JsonElement pathEl))
    {
      if (pathEl.ValueKind != JsonValueKind.String)
      {
        return WrongType(pathEl.ValueKind, "path");
      }

      path = pathEl.GetString()!;
      if (path.Length == 0)
      {
        return Fail(new DomainError("InvalidParameterValue",
            "'path' must be a non-empty string when present."));
      }
    }

    return Result.Success<WorkingDiffInput>(new(scope, path));
  }

  private static Result<WorkingDiffInput> Missing() =>
      Result.Failure<WorkingDiffInput>(new DomainError("MissingParameter",
          "Missing required parameter 'scope'. This tool requires scope."));

  private static Result<WorkingDiffInput> WrongType(JsonValueKind actual, string name = "scope") =>
      Result.Failure<WorkingDiffInput>(new DomainError("InvalidParameterType",
          $"'{name}' must be a string, but got {actual}."));

  private static Result<WorkingDiffInput> Fail(DomainError err) =>
      Result.Failure<WorkingDiffInput>(err);
}
