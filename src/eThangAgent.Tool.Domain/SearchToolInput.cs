using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SearchToolInput(
    string Pattern, bool Regex, string? Path, string? Glob,
    int MaxResults, int ContextLines, bool Clamped)
{
  public const int MaxResultsCap = 200;

  public static Result<SearchToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(
        ["pattern", "mode", "path", "glob", "maxResults", "contextLines", ToolTimeout.ParameterName],
        StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"Allowed: pattern, mode, path, glob, maxResults, contextLines, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty("pattern", out JsonElement patternEl))
    {
      return Missing("pattern");
    }

    if (patternEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("pattern", "string", patternEl.ValueKind);
    }

    string pattern = patternEl.GetString()!;
    if (pattern.Length == 0)
    {
      return Fail(new DomainError("InvalidParameterValue", "'pattern' must be a non-empty string."));
    }

    if (!json.TryGetProperty("mode", out JsonElement modeEl))
    {
      return Missing("mode");
    }

    if (modeEl.ValueKind != JsonValueKind.String)
    {
      return WrongType("mode", "string", modeEl.ValueKind);
    }

    string modeRaw = modeEl.GetString()!;
    bool? regex = modeRaw switch
    {
      "Literal" => false,
      "Regex" => true,
      _ => null,
    };
    if (regex is null)
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'mode' must be exactly \"Literal\" or \"Regex\" (got \"{modeRaw}\")."));
    }

    string? path = null;
    if (json.TryGetProperty("path", out JsonElement pathEl))
    {
      if (pathEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("path", "string", pathEl.ValueKind);
      }

      path = pathEl.GetString()!;
      if (path.Length == 0)
      {
        return Fail(new DomainError("InvalidParameterValue", "'path' must be a non-empty string when present."));
      }
    }

    string? glob = null;
    if (json.TryGetProperty("glob", out JsonElement globEl))
    {
      if (globEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("glob", "string", globEl.ValueKind);
      }

      glob = globEl.GetString()!;
      if (glob.Length == 0)
      {
        return Fail(new DomainError("InvalidParameterValue", "'glob' must be a non-empty string when present."));
      }
    }

    if (!json.TryGetProperty("maxResults", out JsonElement maxEl))
    {
      return Missing("maxResults");
    }

    if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out int max))
    {
      return WrongType("maxResults", "integer", maxEl.ValueKind);
    }

    if (max < 1)
    {
      return Fail(new DomainError("InvalidParameterValue",
          $"'maxResults' must be ≥ 1 (got {max})."));
    }

    int clampedMax = Math.Min(max, MaxResultsCap);

    int contextLines = 0;
    if (json.TryGetProperty("contextLines", out JsonElement ctxEl))
    {
      if (ctxEl.ValueKind != JsonValueKind.Number || !ctxEl.TryGetInt32(out contextLines))
      {
        return WrongType("contextLines", "integer", ctxEl.ValueKind);
      }

      if (contextLines < 0)
      {
        return Fail(new DomainError("InvalidParameterValue",
            $"'contextLines' must be ≥ 0 (got {contextLines})."));
      }
    }

    return Result.Success<SearchToolInput>(new(
        pattern, regex.Value, path, glob, clampedMax, contextLines, Clamped: clampedMax != max));
  }

  private static Result<SearchToolInput> Missing(string n) =>
      Result.Failure<SearchToolInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires pattern, mode, and maxResults."));

  private static Result<SearchToolInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<SearchToolInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<SearchToolInput> Fail(DomainError err) =>
      Result.Failure<SearchToolInput>(err);
}
