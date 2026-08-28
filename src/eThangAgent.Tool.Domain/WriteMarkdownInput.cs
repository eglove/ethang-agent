using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly validated input for <see cref="WriteMarkdownTool"/>. 'document' is
/// always required and parsed through <see cref="MarkdownDocumentParser"/>; 'path' and
/// 'overwrite' stand or fall together - a file target demands the explicit overwrite gate,
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

    HashSet<string> known = new(["path", "document", "overwrite", ToolTimeout.ParameterName], StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, document, overwrite, {ToolTimeout.ParameterName}."));
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

    string? path = null;
    bool? overwrite = null;

    if (json.TryGetProperty("path", out JsonElement pathEl))
    {
      if (pathEl.ValueKind != JsonValueKind.String)
      {
        return Fail(new DomainError("InvalidParameterType", "'path' must be a string."));
      }

      path = pathEl.GetString()!;
      if (path.Length == 0)
      {
        return Fail(new DomainError("InvalidParameterValue", "'path' must be a non-empty string."));
      }

      if (!json.TryGetProperty("overwrite", out JsonElement owEl))
      {
        return Fail(new DomainError("MissingParameter",
            "'overwrite' is required when 'path' is present (true replaces an existing file, false refuses)."));
      }

      if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      {
        return Fail(new DomainError("InvalidParameterType", "'overwrite' must be a boolean."));
      }

      overwrite = owEl.GetBoolean();
    }
    else if (json.TryGetProperty("overwrite", out _))
    {
      return Fail(new DomainError("UnknownParameter",
          "'overwrite' is only valid together with 'path'; without a file target the rendered markdown is returned instead."));
    }

    return Result.Success<WriteMarkdownInput>(new(parsedDoc.Value, path, overwrite));
  }

  private static Result<WriteMarkdownInput> Fail(DomainError err) =>
      Result.Failure<WriteMarkdownInput>(err);
}
