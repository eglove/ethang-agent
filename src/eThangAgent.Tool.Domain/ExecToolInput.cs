using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ExecToolInput(string Program)
{
  public static Result<ExecToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Failure(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["program", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Failure(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: program, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("program", out JsonElement programEl))
    {
      return Failure(new DomainError("MissingParameter",
          "Missing required parameter 'program'."));
    }

    if (programEl.ValueKind != JsonValueKind.String)
    {
      return Failure(new DomainError("InvalidParameterType",
          $"'program' must be a string, but got {programEl.ValueKind}."));
    }

    string program = programEl.GetString()!;
    return program.Length == 0
      ? Failure(new DomainError("InvalidParameterValue",
          "'program' must be a non-empty string."))
      : Result.Success<ExecToolInput>(new ExecToolInput(program));
  }

  private static Result<ExecToolInput> Failure(DomainError error)
      => Result.Failure<ExecToolInput>(error);
}
