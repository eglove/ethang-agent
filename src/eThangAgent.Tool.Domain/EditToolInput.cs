using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record EditToolInput(string Path, string Old, string New, bool All, int Occurrences)
{
    public static Result<EditToolInput> Create(string jsonArguments)
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

        var known = new HashSet<string>(["path", "old", "new", "all", "occurrences"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, old, new, all, occurrences."));

        if (!json.TryGetProperty("path", out var pathEl)) return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String) return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'path' must be a non-empty string."));

        if (!json.TryGetProperty("old", out var oldEl)) return Missing("old");
        if (oldEl.ValueKind != JsonValueKind.String) return WrongType("old", "string", oldEl.ValueKind);
        var old = oldEl.GetString()!;
        if (old.Length == 0)
            return Fail(new Error("InvalidParameterValue",
                "'old' must be a non-empty string — an empty anchor would match everywhere."));

        if (!json.TryGetProperty("new", out var newEl)) return Missing("new");
        if (newEl.ValueKind != JsonValueKind.String) return WrongType("new", "string", newEl.ValueKind);
        var @new = newEl.GetString()!; // may be empty: deletion is explicit intent

        var hasAll = json.TryGetProperty("all", out var allEl);
        var hasOcc = json.TryGetProperty("occurrences", out var occEl);
        if (hasAll == hasOcc)
            return Fail(new Error("InvalidParameterValue",
                "Provide exactly one of 'all' (boolean true) or 'occurrences' (integer \u2265 1)."));

        bool all;
        int occurrences;
        if (hasAll)
        {
            if (allEl.ValueKind is not JsonValueKind.True)
                return Fail(new Error("InvalidParameterValue",
                    "'all' must be exactly true. Provide exactly one of " +
                    "'all' (boolean true) or 'occurrences' (integer \u2265 1)."));
            all = true;
            occurrences = 0;
        }
        else
        {
            if (occEl.ValueKind != JsonValueKind.Number || !occEl.TryGetInt32(out occurrences))
                return WrongType("occurrences", "integer", occEl.ValueKind);
            if (occurrences < 1)
                return Fail(new Error("InvalidParameterValue",
                    $"'occurrences' must be \u2265 1 (got {occurrences})."));
            all = false;
        }

        return Result<EditToolInput>.Success(new(path, old, @new, all, occurrences));
    }

    private static Result<EditToolInput> Missing(string n) =>
        Result<EditToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{n}'. This tool requires path, old, and new, plus exactly one of 'all' or 'occurrences'."));

    private static Result<EditToolInput> WrongType(string n, string e, JsonValueKind a) =>
        Result<EditToolInput>.Failure(new Error("InvalidParameterType",
            $"'{n}' must be a {e}, but got {a}."));

    private static Result<EditToolInput> Fail(Error err) =>
        Result<EditToolInput>.Failure(err);
}
