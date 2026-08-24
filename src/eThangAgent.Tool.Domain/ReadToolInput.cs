using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record ReadToolInput(string Path, int StartLine, int EndLine)
{
    public const int MaxRangeLines = 1000;

    public static Result<ReadToolInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Failure(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["path", "startLine", "endLine", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Failure(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, startLine, endLine, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("path", out var pathEl))
            return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String)
            return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Failure(new Error("InvalidParameterValue",
                "'path' must be a non-empty string."));

        if (!json.TryGetProperty("startLine", out var startEl))
            return Missing("startLine");
        if (startEl.ValueKind != JsonValueKind.Number || !startEl.TryGetInt32(out var startLine))
            return WrongType("startLine", "integer", startEl.ValueKind);

        if (!json.TryGetProperty("endLine", out var endEl))
            return Missing("endLine");
        if (endEl.ValueKind != JsonValueKind.Number || !endEl.TryGetInt32(out var endLine))
            return WrongType("endLine", "integer", endEl.ValueKind);

        if (startLine < 1)
            return Failure(new Error("InvalidParameterValue",
                $"'startLine' must be ≥ 1 (got {startLine})."));
        if (endLine < 1)
            return Failure(new Error("InvalidParameterValue",
                $"'endLine' must be ≥ 1 (got {endLine})."));
        if (startLine > endLine)
            return Failure(new Error("InvalidParameterValue",
                $"'startLine' ({startLine}) must not exceed 'endLine' ({endLine})."));

        var span = (long)endLine - startLine + 1;
        if (span > MaxRangeLines)
            return Failure(new Error("RangeTooLarge",
                $"Range spans {span} lines; maximum is {MaxRangeLines}. " +
                $"Read in chunks (e.g. {startLine}-{startLine + MaxRangeLines - 1}, " +
                $"{startLine + MaxRangeLines}-{Math.Min(startLine + 2 * MaxRangeLines - 1, endLine)})."));

        return Result<ReadToolInput>.Success(new ReadToolInput(path, startLine, endLine));
    }

    private static Result<ReadToolInput> Missing(string name) =>
        Result<ReadToolInput>.Failure(new Error("MissingParameter",
            $"Missing required parameter '{name}'. This tool requires path, startLine, and endLine."));

    private static Result<ReadToolInput> WrongType(string name, string expected, JsonValueKind actual) =>
        Result<ReadToolInput>.Failure(new Error("InvalidParameterType",
            $"'{name}' must be a {expected}, but got {actual}."));

    private static Result<ReadToolInput> Failure(Error error) =>
        Result<ReadToolInput>.Failure(error);
}
