using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ReadToolInput(string Path, int StartLine, int EndLine)
{
  public const int MaxRangeLines = 1000;

  public static Result<ReadToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Failure(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["path", "startLine", "endLine", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Failure(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, startLine, endLine, {ToolTimeout.ParameterName}."));
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
      return Failure(new DomainError("InvalidParameterValue",
          "'path' must be a non-empty string."));
    }

    if (!json.TryGetProperty("startLine", out JsonElement startEl))
    {
      return Missing("startLine");
    }

    if (startEl.ValueKind != JsonValueKind.Number || !startEl.TryGetInt32(out int startLine))
    {
      return WrongType("startLine", "integer", startEl.ValueKind);
    }

    if (!json.TryGetProperty("endLine", out JsonElement endEl))
    {
      return Missing("endLine");
    }

    if (endEl.ValueKind != JsonValueKind.Number || !endEl.TryGetInt32(out int endLine))
    {
      return WrongType("endLine", "integer", endEl.ValueKind);
    }

    if (startLine < 1)
    {
      return Failure(new DomainError("InvalidParameterValue",
          $"'startLine' must be ≥ 1 (got {startLine})."));
    }

    if (endLine < 1)
    {
      return Failure(new DomainError("InvalidParameterValue",
          $"'endLine' must be ≥ 1 (got {endLine})."));
    }

    if (startLine > endLine)
    {
      return Failure(new DomainError("InvalidParameterValue",
          $"'startLine' ({startLine}) must not exceed 'endLine' ({endLine})."));
    }

    long span = (long)endLine - startLine + 1;
    return span > MaxRangeLines
      ? Failure(new DomainError("RangeTooLarge",
          $"Range spans {span} lines; maximum is {MaxRangeLines}. " +
          $"Read in chunks (e.g. {startLine}-{startLine + MaxRangeLines - 1}, " +
          $"{startLine + MaxRangeLines}-{Math.Min(startLine + (2 * MaxRangeLines) - 1, endLine)})."))
      : Result.Success<ReadToolInput>(new ReadToolInput(path, startLine, endLine));
  }

  private static Result<ReadToolInput> Missing(string name) =>
      Result.Failure<ReadToolInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{name}'. This tool requires path, startLine, and endLine."));

  private static Result<ReadToolInput> WrongType(string name, string expected, JsonValueKind actual) =>
      Result.Failure<ReadToolInput>(new DomainError("InvalidParameterType",
          $"'{name}' must be a {expected}, but got {actual}."));

  private static Result<ReadToolInput> Failure(DomainError error) =>
      Result.Failure<ReadToolInput>(error);
}
