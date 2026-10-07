using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strict input for the read_image tool (issue #20): exactly one parameter
///     beyond the mandatory timeout envelope - the image file's path. Validation
///     mirrors ReadToolInput's discipline: unknown parameters rejected, types exact,
///     nothing coerced.</summary>
public sealed record ReadImageToolInput(string Path)
{
  public static Result<ReadImageToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<ReadImageToolInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["path", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Result.Failure<ReadImageToolInput>(new DomainError(ToolErrorCodes.UnknownParameter,
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return Result.Failure<ReadImageToolInput>(new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'path'. This tool requires path."));
    }

    if (pathEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<ReadImageToolInput>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'path' must be a string, but got {pathEl.ValueKind}."));
    }

    string path = pathEl.GetString()!;
    return path.Length == 0
      ? Result.Failure<ReadImageToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            "'path' must be a non-empty string."))
      : Result.Success(new ReadImageToolInput(path));
  }
}
