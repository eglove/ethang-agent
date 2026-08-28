using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SearchToolInput(
    string Pattern, bool Regex, string? Path, string? Glob,
    int MaxResults, int ContextLines, bool Clamped)
{
  public const int MaxResultsCap = 200;

  private const string PatternName = "pattern";
  private const string ModeName = "mode";
  private const string PathName = "path";
  private const string GlobName = "glob";
  private const string MaxResultsName = "maxResults";
  private const string ContextLinesName = "contextLines";
  private const string RequiredParamsText = "This tool requires pattern, mode, and maxResults.";

  private static readonly string[] AllowedNames =
      [PatternName, ModeName, PathName, GlobName, MaxResultsName, ContextLinesName, ToolTimeout.ParameterName];

  public static Result<SearchToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, AllowedNames);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    Result<string> pattern = ParsePattern(json);
    if (!pattern.IsSuccess)
    {
      return Fail(pattern.Error!);
    }

    Result<bool> mode = ParseMode(json);
    if (!mode.IsSuccess)
    {
      return Fail(mode.Error!);
    }

    Result<string?> path = ParseOptionalNonEmpty(json, PathName);
    if (!path.IsSuccess)
    {
      return Fail(path.Error!);
    }

    Result<string?> glob = ParseOptionalNonEmpty(json, GlobName);
    if (!glob.IsSuccess)
    {
      return Fail(glob.Error!);
    }

    Result<int> max = ParseMaxResults(json);
    if (!max.IsSuccess)
    {
      return Fail(max.Error!);
    }

    Result<int> contextLines = ParseContextLines(json);
    if (!contextLines.IsSuccess)
    {
      return Fail(contextLines.Error!);
    }

    int clampedMax = Math.Min(max.Value, MaxResultsCap);
    SearchToolInput input = new(
        pattern.Value!, mode.Value, path.Value, glob.Value, clampedMax, contextLines.Value,
        Clamped: clampedMax != max.Value);
    return Result.Success(input);
  }

  private static Result<string> ParsePattern(JsonElement json)
  {
    Result<string> pattern = ToolArguments.RequireString(json, PatternName, RequiredParamsText);
    if (!pattern.IsSuccess)
    {
      return pattern;
    }

    Result<string> nonEmpty = pattern.Value.Length > 0
      ? pattern
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'pattern' must be a non-empty string."));
    return nonEmpty;
  }

  private static Result<bool> ParseMode(JsonElement json)
  {
    Result<string> text = ToolArguments.RequireString(json, ModeName, RequiredParamsText);
    if (!text.IsSuccess)
    {
      return Result.Failure<bool>(text.Error);
    }

    bool? regex = text.Value switch
    {
      "Literal" => false,
      "Regex" => true,
      _ => null,
    };
    Result<bool> mode = regex is { } parsed
      ? Result.Success(parsed)
      : Result.Failure<bool>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{ModeName}' must be exactly \"Literal\" or \"Regex\" (got \"{text.Value}\")."));
    return mode;
  }

  /// <summary>Reads an optional string that must be non-empty when present.</summary>
  private static Result<string?> ParseOptionalNonEmpty(JsonElement json, string name)
  {
    Result<string?> text = ToolArguments.OptionalString(json, name);
    if (!text.IsSuccess)
    {
      return text;
    }

    if (text.ValueOrNull is null)
    {
      return Result.Success<string?>(null);
    }

    Result<string?> nonEmpty = text.Value.Length > 0
      ? text
      : Result.Failure<string?>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{name}' must be a non-empty string when present."));
    return nonEmpty;
  }

  private static Result<int> ParseMaxResults(JsonElement json)
  {
    Result<int> max = ToolArguments.RequireInt(json, MaxResultsName, RequiredParamsText);
    if (!max.IsSuccess)
    {
      return max;
    }

    Result<int> bounded = max.Value >= 1
      ? max
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{MaxResultsName}' must be ≥ 1 (got {max.Value})."));
    return bounded;
  }

  private static Result<int> ParseContextLines(JsonElement json)
  {
    Result<int?> parsed = ToolArguments.OptionalInt(json, ContextLinesName);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<int>(parsed.Error);
    }

    if (parsed.Value is not { } lines)
    {
      return Result.Success(0);
    }

    Result<int> nonNegative = lines >= 0
      ? Result.Success(lines)
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{ContextLinesName}' must be ≥ 0 (got {lines})."));
    return nonNegative;
  }

  private static Result<SearchToolInput> Fail(DomainError err) =>
      Result.Failure<SearchToolInput>(err);
}
