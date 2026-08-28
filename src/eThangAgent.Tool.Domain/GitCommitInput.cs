using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>
///     Shape-only parsing for git_commit. Style and description are required keys;
///     type, scope, emoji_key, and body are optional. All semantic rules (style
///     legality, type sets, emoji lookup, length limits) belong to
///     <see cref="CommitMessage.Create"/> — their error codes surface verbatim.
/// </summary>
public sealed record GitCommitInput(
    string Style, string? Type, string? Scope, string? EmojiKey,
    string Description, string? Body)
{
  private const string StyleName = "style";
  private const string DescriptionName = "description";
  public static Result<GitCommitInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error!);
    }

    JsonElement json = baseParse.Value;

    HashSet<string> known = new(
        [StyleName, "type", "scope", "emoji_key", DescriptionName, "body", ToolTimeout.ParameterName],
        StringComparer.Ordinal);
    List<string> unknown = [.. json.EnumerateObject()
        .Where(p => !known.Contains(p.Name))
        .Select(p => p.Name)];
    if (unknown.Count > 0)
    {
      return Fail(new DomainError("UnknownParameter",
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          $"Allowed: style, type, scope, emoji_key, description, body, {ToolTimeout.ParameterName}."));
    }

    if (!json.TryGetProperty(StyleName, out JsonElement styleEl))
    {
      return Missing(StyleName);
    }

    if (styleEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(StyleName, styleEl.ValueKind);
    }

    string style = styleEl.GetString()!;

    string? type = null;
    if (json.TryGetProperty("type", out JsonElement typeEl))
    {
      if (typeEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("type", typeEl.ValueKind);
      }

      type = typeEl.GetString()!;
    }

    string? scope = null;
    if (json.TryGetProperty("scope", out JsonElement scopeEl))
    {
      if (scopeEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("scope", scopeEl.ValueKind);
      }

      scope = scopeEl.GetString()!;
    }

    string? emojiKey = null;
    if (json.TryGetProperty("emoji_key", out JsonElement emojiEl))
    {
      if (emojiEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("emoji_key", emojiEl.ValueKind);
      }

      emojiKey = emojiEl.GetString()!;
    }

    if (!json.TryGetProperty(DescriptionName, out JsonElement descEl))
    {
      return Missing(DescriptionName);
    }

    if (descEl.ValueKind != JsonValueKind.String)
    {
      return WrongType(DescriptionName, descEl.ValueKind);
    }

    string description = descEl.GetString()!;

    string? body = null;
    if (json.TryGetProperty("body", out JsonElement bodyEl))
    {
      if (bodyEl.ValueKind != JsonValueKind.String)
      {
        return WrongType("body", bodyEl.ValueKind);
      }

      body = bodyEl.GetString()!;
    }

    return Result.Success<GitCommitInput>(
        new(style, type, scope, emojiKey, description, body));
  }

  private static Result<GitCommitInput> Missing(string n) =>
      Result.Failure<GitCommitInput>(new DomainError("MissingParameter",
          $"Missing required parameter '{n}'. This tool requires style and description."));

  private static Result<GitCommitInput> WrongType(string n, JsonValueKind actual) =>
      Result.Failure<GitCommitInput>(new DomainError("InvalidParameterType",
          $"'{n}' must be a string, but got {actual}."));

  private static Result<GitCommitInput> Fail(DomainError err) =>
      Result.Failure<GitCommitInput>(err);
}
