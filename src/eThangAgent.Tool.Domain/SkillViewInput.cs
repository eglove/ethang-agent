using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SkillViewInput(string Name)
{
  public static Result<SkillViewInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["name", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: name, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("name", out JsonElement nameEl))
    {
      return Missing();
    }

    if (nameEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(nameEl.ValueKind);
    }

    string name = nameEl.GetString()!;
    return name.Length == 0
      ? Fail(new DomainError("InvalidParameterValue", "'name' must be a non-empty string."))
      : Result.Success<SkillViewInput>(new(name));
  }

  private static Result<SkillViewInput> Missing() =>
      Result.Failure<SkillViewInput>(new DomainError("MissingParameter",
          "Missing required parameter 'name'. This tool requires name."));

  private static Result<SkillViewInput> WrongType(JsonValueKind actual) =>
      Result.Failure<SkillViewInput>(new DomainError("InvalidParameterType",
          $"'name' must be a string, but got {actual}."));

  private static Result<SkillViewInput> Fail(DomainError err) =>
      Result.Failure<SkillViewInput>(err);
}
