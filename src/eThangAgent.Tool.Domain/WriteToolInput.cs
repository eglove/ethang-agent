using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed record WriteToolInput(string Path, string Content, bool Overwrite)
{
    public static Result<WriteToolInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Fail(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["path", "content", "overwrite", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, content, overwrite, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("path", out var pathEl))
            return Missing("path");
        if (pathEl.ValueKind != JsonValueKind.String)
            return WrongType("path", "string", pathEl.ValueKind);
        var path = pathEl.GetString()!;
        if (path.Length == 0)
            return Fail(new Error("InvalidParameterValue",
                "'path' must be a non-empty string."));

        if (!json.TryGetProperty("content", out var contentEl))
            return Missing("content");
        if (contentEl.ValueKind != JsonValueKind.String)
            return WrongType("content", "string", contentEl.ValueKind);
        // Content may be empty — an explicitly empty file is a legitimate write.
        var content = contentEl.GetString()!;

        if (!json.TryGetProperty("overwrite", out var owEl))
            return Missing("overwrite");
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
