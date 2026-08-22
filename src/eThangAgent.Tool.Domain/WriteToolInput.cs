using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WriteToolInput(string Path, string Content, bool Overwrite)
{
    public static Result<WriteToolInput> Create(string jsonArguments)
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

        var known = new HashSet<string>(["path", "content", "overwrite"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, content, overwrite."));

        if (!json.TryGetProperty("path", out var pathEl)) return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String) return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'path' must be a non-empty string."));

        if (!json.TryGetProperty("content", out var contentEl)) return Missing("content");
        if (contentEl.ValueKind != JsonValueKind.String) return WrongType("content", "string", contentEl.ValueKind);
        var content = contentEl.GetString()!;

        if (!json.TryGetProperty("overwrite", out var owEl)) return Missing("overwrite");
        if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return WrongType("overwrite", "boolean", owEl.ValueKind);
        var overwrite = owEl.GetBoolean();

        return Result<WriteToolInput>.Success(new(path, content, overwrite));
    }

    private static Result<WriteToolInput> Missing(string n) =>
        Result<WriteToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires path, content, and overwrite."));

    private static Result<WriteToolInput> WrongType(string n, string e, JsonValueKind a) =>
        Result<WriteToolInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a {e}, but got {a}."));

    private static Result<WriteToolInput> Fail(Error err) =>
        Result<WriteToolInput>.Failure(err);
}
