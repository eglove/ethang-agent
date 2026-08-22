using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteTool : ITool
{
    private readonly WorkspacePathResolver _resolver;
    private readonly IFileWriteAccess _files;

    public ToolDefinition Definition { get; } = new(
        "write",
        "Create or replace a text file. path, content, and overwrite are all mandatory; " +
        "the call fails if the file exists unless overwrite is exactly true \u2014 it will never " +
        "silently replace anything. Parent directories are never created automatically; create " +
        "them first if needed. Paths resolve inside the workspace; escapes are rejected. Content " +
        "is written verbatim as UTF-8 without BOM (an empty string writes an empty file). Output " +
        "is a single annotation line in [brackets]: `[write <path>] created|overwritten, N bytes` \u2014 " +
        "metadata, not file content. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter("path", ToolParameterType.String,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("content", ToolParameterType.String,
                "Exact file content. May be empty."),
            new ToolParameter("overwrite", ToolParameterType.Boolean,
                "true to replace an existing file, false to refuse."),
        ]);

    public WriteTool(WorkspacePathResolver resolver, IFileWriteAccess files)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = WriteToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Err(parsed.Error!);

        var resolved = _resolver.Resolve(parsed.Value!.Path);
        if (!resolved.IsSuccess)
            return Err(resolved.Error!);

        var written = await _files.WriteFileAsync(
            resolved.Value!, parsed.Value.Content, parsed.Value.Overwrite, ct);
        if (!written.IsSuccess)
            return Err(written.Error!);

        var o = written.Value!;
        return new ToolResult(
            $"[write {resolved.Value}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes",
            false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
