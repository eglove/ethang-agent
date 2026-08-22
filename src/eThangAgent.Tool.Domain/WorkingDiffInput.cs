using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WorkingDiffInput(string Scope, string? Path)
{
    public static Result<WorkingDiffInput> Create(string jsonArguments)
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

        var known = new HashSet<string>(["scope", "path"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: scope, path."));

        if (!json.TryGetProperty("scope", out var scopeEl)) return Missing();
        if (scopeEl.ValueKind != JsonValueKind.String) return WrongType(scopeEl.ValueKind);
        var scope = scopeEl.GetString()!;
        if (scope is not ("Staged" or "Unstaged" or "All"))
            return Fail(new Error("InvalidParameterValue",
                $"'scope' must be exactly \"Staged\", \"Unstaged\", or \"All\" " +
                $"(case-sensitive; got \"{scope}\")."));

        string? path = null;
        if (json.TryGetProperty("path", out var pathEl))
        {
            if (pathEl.ValueKind != JsonValueKind.String) return WrongType(pathEl.ValueKind, "path");
            path = pathEl.GetString()!;
            if (path.Length == 0)
                return Fail(new Error("InvalidParameterValue",
                    "'path' must be a non-empty string when present."));
        }

        return Result<WorkingDiffInput>.Success(new(scope, path));
    }

    private static Result<WorkingDiffInput> Missing() =>
        Result<WorkingDiffInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'scope'. This tool requires scope."));

    private static Result<WorkingDiffInput> WrongType(JsonValueKind actual, string name = "scope") =>
        Result<WorkingDiffInput>.Failure(new Error("InvalidParameterType",
            $"'{name}' must be a string, but got {actual}."));

    private static Result<WorkingDiffInput> Fail(Error err) =>
        Result<WorkingDiffInput>.Failure(err);
}
