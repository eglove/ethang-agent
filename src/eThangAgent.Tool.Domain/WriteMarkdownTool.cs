using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteMarkdownTool : ITool
{
    private readonly IPathResolver _resolver;
    private readonly IFileWriteAccess _files;

    public ToolDefinition Definition { get; } = new(
        "write_markdown",
        "Render a structured JSON document into well-formed markdown deterministically - no manual escaping of fences, pipes, or frontmatter. " +
        "'document' is a JSON object: { \"blocks\": [ ... ], \"frontmatter\": { ... } (optional) }. Block types (each object carries \"type\"): " +
        "text {text}, header {level 1-3, text}, quote {text}, alert {alertType CAUTION|IMPORTANT|NOTE|TIP|WARNING, text}, " +
        "codeBlock {code, language?}, unorderedList/numberedList {items:[{text, children?}]}, taskList {items:[{label, isComplete}]}, " +
        "table {headers:[string | {text, align? left|center|right}], rows:[[string]] - row length must equal header count}, space {count? >=1}. " +
        "A block array entry may be null (skipped). Frontmatter values are string/number/boolean only, single-line. " +
        "Without 'path', the rendered markdown is returned verbatim as the result (usable for db/state entries). With optional 'path', the " +
        "markdown is written to that workspace file through the same gate as write: 'overwrite' becomes required when 'path' is present and " +
        "the call fails on an existing file unless overwrite is exactly true; parent directories are never created. Output in write mode is a " +
        "single annotation line `[write_markdown <path>] created|overwritten, N bytes`. Errors begin with `Error [Code]:`.",
        [
            new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.Integer, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("document", ToolParameterType.String,
                "The structured markdown document: {\"blocks\": [...], \"frontmatter\": {...}?}. See description for exact block shapes."),
            new ToolParameter("path", ToolParameterType.String,
                "Optional. File target, workspace-relative or absolute-inside-workspace. Omit to receive the rendered markdown as the result."),
            new ToolParameter("overwrite", ToolParameterType.Boolean,
                "Optional but required when 'path' is present: true to replace an existing file, false to refuse."),
        ],
        ["timeoutSeconds", "document"]);

    public WriteMarkdownTool(IPathResolver resolver, IFileWriteAccess files)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
        var parsed = WriteMarkdownInput.Create(input.JsonArguments);
        if (!parsed.IsSuccess)
            return Task.FromResult(Err(parsed.Error!));

        var resolvedPath = parsed.Value!.Path is null
            ? Result<string>.Success("")
            : _resolver.Resolve(parsed.Value.Path);
        if (!resolvedPath.IsSuccess)
            return Task.FromResult(Err(resolvedPath.Error!));

        var budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
        if (!budget.IsSuccess)
            return Task.FromResult(Err(budget.Error!));

        return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
            RenderAndMaybeWriteAsync(resolvedPath.Value!, parsed.Value, token), ct);
    }

    private async Task<ToolResult> RenderAndMaybeWriteAsync(string resolvedPath, WriteMarkdownInput args, CancellationToken ct)
    {
        var rendered = MarkdownRenderer.Render(args.Document);

        if (args.Path is null)
            return new ToolResult(rendered, false);

        var written = await _files.WriteFileAsync(resolvedPath, rendered, args.Overwrite!.Value, ct);
        if (!written.IsSuccess)
            return Err(written.Error!);

        var o = written.Value!;
        return new ToolResult(
            $"[write_markdown {resolvedPath}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes",
            false);
    }

    private static ToolResult Err(Error error) => new($"Error [{error.Code}]: {error.Message}", true);
}
