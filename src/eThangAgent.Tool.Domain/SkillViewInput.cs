using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SkillViewInput(string Name)
{
    public static Result<SkillViewInput> Create(string jsonArguments)
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

        var known = new HashSet<string>(["name"], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: name."));

        if (!json.TryGetProperty("name", out var nameEl)) return Missing();
        if (nameEl.ValueKind != JsonValueKind.String) return WrongType(nameEl.ValueKind);
        var name = nameEl.GetString()!;
        if (name.Length == 0)
            return Fail(new Error("InvalidParameterValue", "'name' must be a non-empty string."));

        return Result<SkillViewInput>.Success(new(name));
    }

    private static Result<SkillViewInput> Missing() =>
        Result<SkillViewInput>.Failure(new Error("MissingParameter",
            "Missing required parameter 'name'. This tool requires name."));

    private static Result<SkillViewInput> WrongType(JsonValueKind actual) =>
        Result<SkillViewInput>.Failure(new Error("InvalidParameterType",
            $"'name' must be a string, but got {actual}."));

    private static Result<SkillViewInput> Fail(Error err) =>
        Result<SkillViewInput>.Failure(err);
}
