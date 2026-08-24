using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ExecToolInput(string Program)
{
    public static Result<ExecToolInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Failure(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["program", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Failure(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: program, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("program", out var programEl))
            return Failure(new Error("MissingParameter",
                "Missing required parameter 'program'."));
        if (programEl.ValueKind != JsonValueKind.String)
            return Failure(new Error("InvalidParameterType",
                $"'program' must be a string, but got {programEl.ValueKind}."));
        var program = programEl.GetString()!;
        if (program.Length == 0)
            return Failure(new Error("InvalidParameterValue",
                "'program' must be a non-empty string."));

        return Result<ExecToolInput>.Success(new ExecToolInput(program));
    }

    private static Result<ExecToolInput> Failure(Error error)
        => Result<ExecToolInput>.Failure(error);
}
