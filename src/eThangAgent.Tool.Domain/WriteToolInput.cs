using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WriteToolInput(string Path, string Content, bool Overwrite)
{
  private const string ContentName = "content";
  private const string LinesName = "lines";
  private const string OverwriteName = "overwrite";

  public static Result<WriteToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["path", ContentName, LinesName, OverwriteName, ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, content or lines (exactly one), overwrite, {ToolTimeout.ParameterName}."));
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
      return Fail(new DomainError("InvalidParameterValue",
          "'path' must be a non-empty string."));
    }

    Result<string> content = ParseContentSelector(json);
    if (!content.IsSuccess)
    {
      return Fail(content.Error);
    }

    // Omitted 'overwrite' defaults to refusing replacement: the call stays a create-only
    // write and an existing file fails with FileExists. Replacement still requires the
    // explicit opt-in, so nothing is ever silently replaced.
    if (!json.TryGetProperty(OverwriteName, out JsonElement owEl))
    {
      return Result.Success<WriteToolInput>(new(path, content.Value, Overwrite: false));
    }

    if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
      return WrongType(OverwriteName, "boolean", owEl.ValueKind);
    }

    bool overwrite = owEl.GetBoolean();

    return Result.Success<WriteToolInput>(new(path, content.Value, overwrite));
  }

  /// <summary>Exactly one of 'content' (string) or 'lines' (string array, joined with LF).
  /// An empty 'lines' array is the explicit empty file; an empty string inside 'lines' is a
  /// blank line. 'lines' exists because a JSON array of lines removes the raw-string
  /// escaping hazard end-to-end: each element is carried verbatim, no quoting inside quoting.</summary>
  private static Result<string> ParseContentSelector(JsonElement json)
  {
    bool hasContent = json.TryGetProperty(ContentName, out JsonElement contentEl);
    bool hasLines = json.TryGetProperty(LinesName, out JsonElement linesEl);
    if (hasContent == hasLines)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Provide exactly one of 'content' (string) or 'lines' (array of strings, joined with LF)."));
    }

    if (hasContent)
    {
      return contentEl.ValueKind != JsonValueKind.String
        ? Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{ContentName}' must be a string, but got {contentEl.ValueKind}."))
        : Result.Success(contentEl.GetString()!);
    }

    if (linesEl.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{LinesName}' must be an array of strings, but got {linesEl.ValueKind}."));
    }

    List<string> lines = [];
    int index = 0;
    foreach (JsonElement item in linesEl.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{LinesName}[{index}]' must be a string, but got {item.ValueKind}."));
      }
      lines.Add(item.GetString()!);
      index++;
    }

    return Result.Success(string.Join("\n", lines));
  }

  private static Result<WriteToolInput> Missing(string n) =>
      Result.Failure<WriteToolInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires path and exactly one of content or lines; 'overwrite' is optional and defaults to refusing replacement of an existing file."));

  private static Result<WriteToolInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<WriteToolInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<WriteToolInput> Fail(DomainError err) =>
      Result.Failure<WriteToolInput>(err);
}
