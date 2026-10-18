using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly validated input for <see cref="WriteMarkdownTool"/>. 'document' is
/// always required and parsed through <see cref="MarkdownDocumentParser"/>; 'path' and
/// 'overwrite' stand or fall together - a file target honors the overwrite gate,
/// and the gate is meaningless (therefore rejected) without a target.</summary>
public sealed record WriteMarkdownInput(
    MarkdownDocument Document,
    string? Path,
    bool? Overwrite)
{
  public static Result<WriteMarkdownInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    DomainError? unknown = RejectUnknown(json);
    if (unknown is not null)
    {
      return Fail(unknown);
    }

    if (!json.TryGetProperty("document", out JsonElement docEl))
    {
      return Fail(new DomainError("MissingParameter",
          "Missing required parameter 'document'. This tool requires timeoutSeconds and document."));
    }

    Result<MarkdownDocument> parsedDoc = MarkdownDocumentParser.Parse(docEl, "document");
    if (!parsedDoc.IsSuccess)
    {
      return Fail(parsedDoc.Error);
    }

    Result<(string? Path, bool? Overwrite)> target = ParseTarget(json);
    return target.IsSuccess
      ? Result.Success<WriteMarkdownInput>(new(parsedDoc.Value, target.Value.Path, target.Value.Overwrite))
      : Fail(target.Error);
  }

  private static DomainError? RejectUnknown(JsonElement json)
  {
    HashSet<string> known = new(["path", "document", "overwrite", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    return unknown.Count > 0
      ? new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, document, overwrite, {ToolTimeout.ParameterName}.")
      : null;
  }

  /// <summary>'path' and 'overwrite' stand or fall together: a file target honors the
  ///     overwrite gate (defaulting to create-only), and the gate alone is rejected.</summary>
  private static Result<(string? Path, bool? Overwrite)> ParseTarget(JsonElement json)
  {
    if (!json.TryGetProperty("path", out JsonElement pathEl))
    {
      return RequireNoOverwrite(json);
    }

    Result<string> path = RequireNonEmptyPath(pathEl);
    if (!path.IsSuccess)
    {
      return Result.Failure<(string?, bool?)>(path.Error);
    }

    // Omitted 'overwrite' defaults to refusing replacement (create-only), matching write.
    return json.TryGetProperty("overwrite", out JsonElement owEl)
      ? RequireBooleanOverwrite(owEl, path.Value)
      : Result.Success<(string?, bool?)>((path.Value, false));
  }

  private static Result<(string? Path, bool? Overwrite)> RequireBooleanOverwrite(JsonElement owEl, string path) =>
      owEl.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? Result.Success<(string?, bool?)>((path, owEl.GetBoolean()))
        : Result.Failure<(string?, bool?)>(new DomainError("InvalidParameterType", "'overwrite' must be a boolean."));

  private static Result<(string? Path, bool? Overwrite)> RequireNoOverwrite(JsonElement json) =>
      json.TryGetProperty("overwrite", out _)
        ? Result.Failure<(string?, bool?)>(new DomainError("UnknownParameter",
            "'overwrite' is only valid together with 'path'; without a file target the rendered markdown is returned instead."))
        : Result.Success<(string?, bool?)>((null, null));

  private static Result<string> RequireNonEmptyPath(JsonElement pathEl)
  {
    if (pathEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError("InvalidParameterType", "'path' must be a string."));
    }

    string path = pathEl.GetString()!;
    return path.Length > 0
      ? Result.Success(path)
      : Result.Failure<string>(new DomainError("InvalidParameterValue", "'path' must be a non-empty string."));
  }

  private static Result<WriteMarkdownInput> Fail(DomainError err) =>
      Result.Failure<WriteMarkdownInput>(err);
}
