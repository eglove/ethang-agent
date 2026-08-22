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
            ["pattern", "mode", "path", "glob", "maxResults", "contextLines"],
            StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
                "Allowed: pattern, mode, path, glob, maxResults, contextLines."));

        if (!json.TryGetProperty("pattern", out var patternEl)) return Missing("pattern");
        if (patternEl.ValueKind != JsonValueKind.String) return WrongType("pattern", "string", patternEl.ValueKind);
        var pattern = patternEl.GetString()!;
        if (pattern.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'pattern' must be a non-empty string."));

        if (!json.TryGetProperty("mode", out var modeEl)) return Missing("mode");
        if (modeEl.ValueKind != JsonValueKind.String) return WrongType("mode", "string", modeEl.ValueKind);
        var modeRaw = modeEl.GetString()!;
        var regex = modeRaw switch
        {
            "Literal" => false,
            "Regex" => true,
            _ => (bool?)null,
        };
        if (regex is null)
            return Fail(new Error("InvalidParameterValue",
                $"'mode' must be exactly \"Literal\" or \"Regex\" (got \"{modeRaw}\")."));

        string? path = null;
        if (json.TryGetProperty("path", out var pathEl))
        {
            if (pathEl.ValueKind != JsonValueKind.String) return WrongType("path", "string", pathEl.ValueKind);
            path = pathEl.GetString()!;
            if (path.Length == 0)
                return Fail(new Error("InvalidParameterValue", "'path' must be a non-empty string when present."));
        }

        string? glob = null;
        if (json.TryGetProperty("glob", out var globEl))
        {
            if (globEl.ValueKind != JsonValueKind.String) return WrongType("glob", "string", globEl.ValueKind);
            glob = globEl.GetString()!;
            if (glob.Length == 0)
                return Fail(new Error("InvalidParameterValue", "'glob' must be a non-empty string when present."));
        }

        if (!json.TryGetProperty("maxResults", out var maxEl)) return Missing("maxResults");
        if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var max))
            return WrongType("maxResults", "integer", maxEl.ValueKind);
        if (max < 1)
            return Fail(new Error("InvalidParameterValue",
                $"'maxResults' must be \u2265 1 (got {max})."));
        var clampedMax = Math.Min(max, MaxResultsCap);

        var contextLines = 0;
        if (json.TryGetProperty("contextLines", out var ctxEl))
        {
            if (ctxEl.ValueKind != JsonValueKind.Number || !ctxEl.TryGetInt32(out contextLines))
                return WrongType("contextLines", "integer", ctxEl.ValueKind);
            if (contextLines < 0)
                return Fail(new Error("InvalidParameterValue",
                    $"'contextLines' must be \u2265 0 (got {contextLines})."));
        }

        return Result<SearchToolInput>.Success(new(
            pattern, regex.Value, path, glob, clampedMax, contextLines, Clamped: clampedMax != max));
    }

    private static Result<SearchToolInput> Missing(string n) =>
        Result<SearchToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires pattern, mode, and maxResults."));

    private static Result<SearchToolInput> WrongType(string n, string e, JsonValueKind a) =>
        Result<SearchToolInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a {e}, but got {a}."));

    private static Result<SearchToolInput> Fail(Error err) =>
        Result<SearchToolInput>.Failure(err);
}
