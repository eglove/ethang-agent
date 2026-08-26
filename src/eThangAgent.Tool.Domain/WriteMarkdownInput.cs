using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly validated input for <see cref="WriteMarkdownTool"/>. 'document' is
/// always required and parsed through <see cref="MarkdownDocumentParser"/>; 'path' and
/// 'overwrite' stand or fall together - a file target demands the explicit overwrite gate,
/// and the gate is meaningless (therefore rejected) without a target.</summary>
public sealed record WriteMarkdownInput(
    MarkdownDocument Document,
    string? Path,
    bool? Overwrite)
{
    public static Result<WriteMarkdownInput> Create(string jsonArguments)
    {
        var baseParse = ToolArguments.ParseObject(jsonArguments);
        if (!baseParse.IsSuccess)
            return Fail(baseParse.Error!);
        var json = baseParse.Value;

        var known = new HashSet<string>(["path", "document", "overwrite", ToolTimeout.ParameterName], StringComparer.Ordinal);
        var unknown = json.EnumerateObject()
            .Where(p => !known.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        if (unknown.Count > 0)
            return Fail(new Error("UnknownParameter",
                $"Unknown parameter(s): {string.Join(", ", unknown)}. Allowed: path, document, overwrite, {ToolTimeout.ParameterName}."));

        if (!json.TryGetProperty("document", out var docEl))
            return Fail(new Error("MissingParameter",
                "Missing required parameter 'document'. This tool requires timeoutSeconds and document."));
        var parsedDoc = MarkdownDocumentParser.Parse(docEl, "document");
        if (!parsedDoc.IsSuccess)
            return Fail(parsedDoc.Error!);

        string? path = null;
        bool? overwrite = null;

        if (json.TryGetProperty("path", out var pathEl))
        {
            if (pathEl.ValueKind != JsonValueKind.String)
                return Fail(new Error("InvalidParameterType", "'path' must be a string."));
            path = pathEl.GetString()!;
            if (path.Length == 0)
                return Fail(new Error("InvalidParameterValue", "'path' must be a non-empty string."));

            if (!json.TryGetProperty("overwrite", out var owEl))
                return Fail(new Error("MissingParameter",
                    "'overwrite' is required when 'path' is present (true replaces an existing file, false refuses)."));
            if (owEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Fail(new Error("InvalidParameterType", "'overwrite' must be a boolean."));
            overwrite = owEl.GetBoolean();
        }
        else if (json.TryGetProperty("overwrite", out var orphanEl))
        {
            return Fail(new Error("UnknownParameter",
                "'overwrite' is only valid together with 'path'; without a file target the rendered markdown is returned instead."));
        }

        return Result<WriteMarkdownInput>.Success(new(parsedDoc.Value!, path, overwrite));
    }

    private static Result<WriteMarkdownInput> Fail(Error err) =>
        Result<WriteMarkdownInput>.Failure(err);
}
