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
    public static Result<GitCommitInput> Create(string jsonArguments)
    {
        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(jsonArguments);
            json = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Fail(new Error("InvalidJsonArguments",
                $"Arguments are not valid JSON: {ex.Message}"));
        }
        if (json.ValueKind != JsonValueKind.Object)
            return Fail(new Error("InvalidJsonArguments", "Arguments must be a JSON object."));

        var known = new HashSet<string>(
            ["style", "type", "scope", "emoji_key", "description", "body"],
            StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                "Allowed: style, type, scope, emoji_key, description, body."));

        if (!json.TryGetProperty("style", out var styleEl)) return Missing("style");
        if (styleEl.ValueKind != JsonValueKind.String) return WrongType("style", styleEl.ValueKind);
        var style = styleEl.GetString()!;

        string? type = null;
        if (json.TryGetProperty("type", out var typeEl))
        {
            if (typeEl.ValueKind != JsonValueKind.String) return WrongType("type", typeEl.ValueKind);
            type = typeEl.GetString()!;
        }

        string? scope = null;
        if (json.TryGetProperty("scope", out var scopeEl))
        {
            if (scopeEl.ValueKind != JsonValueKind.String) return WrongType("scope", scopeEl.ValueKind);
            scope = scopeEl.GetString()!;
        }

        string? emojiKey = null;
        if (json.TryGetProperty("emoji_key", out var emojiEl))
        {
            if (emojiEl.ValueKind != JsonValueKind.String) return WrongType("emoji_key", emojiEl.ValueKind);
            emojiKey = emojiEl.GetString()!;
        }

        if (!json.TryGetProperty("description", out var descEl)) return Missing("description");
        if (descEl.ValueKind != JsonValueKind.String) return WrongType("description", descEl.ValueKind);
        var description = descEl.GetString()!;

        string? body = null;
        if (json.TryGetProperty("body", out var bodyEl))
        {
            if (bodyEl.ValueKind != JsonValueKind.String) return WrongType("body", bodyEl.ValueKind);
            body = bodyEl.GetString()!;
        }

        return Result<GitCommitInput>.Success(
            new(style, type, scope, emojiKey, description, body));
    }

    private static Result<GitCommitInput> Missing(string n) =>
        Result<GitCommitInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires style and description."));

    private static Result<GitCommitInput> WrongType(string n, JsonValueKind actual) =>
        Result<GitCommitInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a string, but got {actual}."));

    private static Result<GitCommitInput> Fail(Error err) =>
        Result<GitCommitInput>.Failure(err);
}
