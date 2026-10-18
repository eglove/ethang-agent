using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ReadToolInput(string Path, int StartLine, int EndLine, bool Force = false)
{
  public const int MaxRangeLines = 1000;

  private const string StartLineName = "startLine";
  private const string EndLineName = "endLine";

  public static Result<ReadToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Failure(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    string path = string.Empty;
    DomainError? input = CheckUnknown(json) ?? TryGetPath(json, out path);
    return input is not null
      ? Failure(input)
      : ValidateLines(json, path);
  }

  private static DomainError? CheckUnknown(JsonElement json)
  {
    HashSet<string> known = new(["path", StartLineName, EndLineName, "force", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    return unknown.Count == 0
      ? null
      : new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, startLine, endLine, force, {ToolTimeout.ParameterName}.");
  }

  private static DomainError? TryGetPath(JsonElement json, out string path)
  {
    path = string.Empty;
    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return Missing("path").Error;
    }

    if (pathEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("path", "string", pathEl.ValueKind).Error;
    }

    path = pathEl.GetString()!;
    return path.Length == 0
      ? new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be a non-empty string.")
      : null;
  }

  private static Result<ReadToolInput> ValidateLines(JsonElement json, string path)
  {
    DomainError? lineNumbers = TryGetLineNumbers(json, out int startLine, out int endLine);
    if (lineNumbers is not null)
    {
      return Failure(lineNumbers);
    }

    DomainError? bounds = CheckBounds(startLine, endLine);
    if (bounds is not null)
    {
      return Failure(bounds);
    }

    DomainError? force = TryGetForce(json, out bool forceValue);
    return force is not null
      ? Failure(force)
      : CheckRange(path, startLine, endLine, forceValue);
  }

  private static DomainError? TryGetLineNumbers(JsonElement json, out int startLine, out int endLine)
  {
    startLine = 0;
    endLine = 0;
    DomainError? start = TryGetStartLine(json, out startLine);
    DomainError? end = start ?? TryGetEndLine(json, out endLine);
    return end;
  }

  private static DomainError? TryGetStartLine(JsonElement json, out int startLine)
  {
    startLine = 0;
    return json.TryGetProperty(StartLineName, out JsonElement startEl)
      ? StartLineKindError(startEl, out startLine)
      : Missing(StartLineName).Error;
  }

  private static DomainError? StartLineKindError(JsonElement startEl, out int startLine)
  {
    startLine = 0;
    return startEl.ValueKind != JsonValueKind.Number || !startEl.TryGetInt32(out startLine)
      ? WrongType(StartLineName, "integer", startEl.ValueKind).Error
      : null;
  }

  private static DomainError? TryGetEndLine(JsonElement json, out int endLine)
  {
    endLine = 0;
    return json.TryGetProperty(EndLineName, out JsonElement endEl)
      ? EndLineKindError(endEl, out endLine)
      : Missing(EndLineName).Error;
  }

  private static DomainError? EndLineKindError(JsonElement endEl, out int endLine)
  {
    endLine = 0;
    return endEl.ValueKind != JsonValueKind.Number || !endEl.TryGetInt32(out endLine)
      ? WrongType(EndLineName, "integer", endEl.ValueKind).Error
      : null;
  }

  private static DomainError? CheckBounds(int startLine, int endLine)
  {
    DomainError? startBound = StartLineBoundError(startLine);
    DomainError? endBound = startBound ?? EndLineBoundError(endLine);
    return endBound ?? LineOrderError(startLine, endLine);
  }

  private static DomainError? StartLineBoundError(int startLine) => startLine < 1
    ? new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"'startLine' must be ≥ 1 (got {startLine}).")
    : null;

  private static DomainError? EndLineBoundError(int endLine) => endLine < 1
    ? new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"'endLine' must be ≥ 1 (got {endLine}).")
    : null;

  private static DomainError? LineOrderError(int startLine, int endLine) => startLine > endLine
    ? new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"'startLine' ({startLine}) must not exceed 'endLine' ({endLine}).")
    : null;

  private static DomainError? TryGetForce(JsonElement json, out bool force)
  {
    force = false;
    if (!json.TryGetProperty("force", out JsonElement forceEl))
    {
      return null;
    }

    if (forceEl.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
    {
      return new DomainError("InvalidParameterType",
          "'force' must be a boolean, but got " + forceEl.ValueKind + ".");
    }

    force = forceEl.GetBoolean();
    return null;
  }

  private static Result<ReadToolInput> CheckRange(string path, int startLine, int endLine, bool force)
  {
    long span = (long)endLine - startLine + 1;
    return span > MaxRangeLines
      ? Failure(new DomainError("RangeTooLarge",
          $"Range spans {span} lines; maximum is {MaxRangeLines}. " +
          $"Read in chunks (e.g. {startLine}-{startLine + MaxRangeLines - 1}, " +
          $"{startLine + MaxRangeLines}-{Math.Min(startLine + (2 * MaxRangeLines) - 1, endLine)})."))
      : Result.Success(new ReadToolInput(path, startLine, endLine, force));
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
