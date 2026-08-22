using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class EditTool : ITool
{
    private readonly WorkspacePathResolver _resolver;
    private readonly IFileEditAccess _files;

    public ToolDefinition Definition { get; } = new(
        "edit",
        "Edit a text file by exact literal replacement. path, old, and new are mandatory; then provide " +
        "exactly one of all (boolean true \u2014 replace every occurrence) or occurrences (integer \u2265 1 \u2014 expected " +
        "match count; the call fails if the actual count differs, naming both numbers). old must appear verbatim \u2014 " +
        "no regex, no whitespace normalization. An empty new deletes the matched text. The file is never created. " +
        "Binary files are refused. Output is a single annotation line: `[edit <path>] replaced N occurrence(s), " +
        "file now M lines`. Errors begin with `Error [Code]:` and are safe to retry with corrected arguments.",
        [
            new ToolParameter("path", ToolParameterType.String,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("old", ToolParameterType.String,
                "Exact text to replace (literal, case-sensitive)."),
            new ToolParameter("new", ToolParameterType.String,
                "Replacement text. May be empty to delete."),
            new ToolParameter("all", ToolParameterType.Boolean,
                "true to replace every occurrence (mutually exclusive with occurrences)."),
            new ToolParameter("occurrences", ToolParameterType.Integer,
                "Expected number of replacements (mutually exclusive with all). Minimum: 1", Minimum: 1),
        ]);

    public EditTool(WorkspacePathResolver resolver, IFileEditAccess files)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = EditToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);

        var resolved = _resolver.Resolve(parsed.Value!.Path);
        if (!resolved.IsSuccess)
            return Err(resolved.Error!);

        var v = parsed.Value;
        var replaced = await _files.ReplaceInFileAsync(
            resolved.Value!, v.Old, v.New, v.All ? null : v.Occurrences, ct);
        if (!replaced.IsSuccess)
            return Err(replaced.Error!);

        var o = replaced.Value!;
        var noun = o.Replaced == 1 ? "occurrence" : "occurrence(s)";
        return new ToolResult(
            $"[edit {resolved.Value}] replaced {o.Replaced} {noun}, file now {o.NewLineCount} lines",
            false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
