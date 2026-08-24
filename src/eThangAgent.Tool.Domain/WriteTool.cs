using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteTool : ITool
{
    private readonly IPathResolver _resolver;
    private readonly IFileWriteAccess _files;

    public ToolDefinition Definition { get; } = new(
        "write",
        "Create or replace a text file. timeoutSeconds, path, content, and overwrite are all mandatory; " +
        "the call fails if the file exists unless overwrite is exactly true — it will never " +
        "silently replace anything. Parent directories are never created automatically; create " +
        "them first if needed. Paths resolve inside the workspace; escapes are rejected. Content " +
        "is written verbatim as UTF-8 without BOM (an empty string writes an empty file). Output " +
        "is a single annotation line in [brackets]: `[write <path>] created|overwritten, N bytes` — " +
        "metadata, not file content. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.String,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("content", ToolParameterType.String,
                "Exact file content. May be empty."),
            new ToolParameter("overwrite", ToolParameterType.Boolean,
                "true to replace an existing file, false to refuse."),
        ]);

    public WriteTool(IPathResolver resolver, IFileWriteAccess files)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = WriteToolInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));

        var resolved = _resolver.Resolve(parsed.Value!.Path);
        if (!resolved.IsSuccess)
            return Task.FromResult(Err(resolved.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            WriteAsync(resolved.Value!, parsed.Value, token), ct);
    }

    private async Task<ToolResult> WriteAsync(string path, WriteToolInput args, CancellationToken ct)
    {
        var written = await _files.WriteFileAsync(path, args.Content, args.Overwrite, ct);
        if (!written.IsSuccess)
            return Err(written.Error!);

        var o = written.Value!;
        return new ToolResult(
            $"[write {path}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes",
            false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
