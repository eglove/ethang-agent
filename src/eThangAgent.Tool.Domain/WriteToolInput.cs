using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WriteToolInput(string Path, string Content, bool Overwrite)
{
  private const string ContentName = "content";
  private const string OverwriteName = "overwrite";
  public static Result<WriteToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(["path", ContentName, OverwriteName, ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, content, overwrite, {ToolTimeout.ParameterName}."));
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

    if (!json.TryGetProperty(ContentName, out JsonElement contentEl))
    {
      return Missing(ContentName);
    }

    if (contentEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(ContentName, "string", contentEl.ValueKind);
    }
    // Content may be empty — an explicitly empty file is a legitimate write.
    string content = contentEl.GetString()!;

    // Omitted 'overwrite' defaults to refusing replacement: the call stays a create-only
    // write and an existing file fails with FileExists. Replacement still requires the
    // explicit opt-in, so nothing is ever silently replaced.
    if (!json.TryGetProperty(OverwriteName, out JsonElement owEl))
    {
      return Result.Success<WriteToolInput>(new(path, content, Overwrite: false));
    }

    if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
    {
      return WrongType(OverwriteName, "boolean", owEl.ValueKind);
    }

    bool overwrite = owEl.GetBoolean();

    return Result.Success<WriteToolInput>(new(path, content, overwrite));
  }

  private static Result<WriteToolInput> Missing(string n) =>
      Result.Failure<WriteToolInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires path and content; 'overwrite' is optional and defaults to refusing replacement of an existing file."));

  private static Result<WriteToolInput> WrongType(string n, string e, JsonValueKind a) =>
      Result.Failure<WriteToolInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a {e}, but got {a}."));

  private static Result<WriteToolInput> Fail(DomainError err) =>
      Result.Failure<WriteToolInput>(err);
}
