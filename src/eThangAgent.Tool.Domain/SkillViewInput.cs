using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record SkillViewInput(string Name)
{
    public static Result<SkillViewInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Fail(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["name", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: name, {ToolTimeout.ParameterName}."));

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
