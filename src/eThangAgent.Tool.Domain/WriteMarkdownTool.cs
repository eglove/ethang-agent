using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteMarkdownTool(IPathResolver resolver, IFileWriteAccess files) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileWriteAccess _files = files ?? throw new ArgumentNullException(nameof(files));

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
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("document", ToolParameterType.Text,
                "The structured markdown document: {\"blocks\": [...], \"frontmatter\": {...}?}. See description for exact block shapes."),
            new ToolParameter("path", ToolParameterType.Text,
                "Optional. File target, workspace-relative or absolute-inside-workspace. Omit to receive the rendered markdown as the result."),
            new ToolParameter("overwrite", ToolParameterType.Flag,
                "Optional but required when 'path' is present: true to replace an existing file, false to refuse."),
      ],
      ["timeoutSeconds", "document"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<WriteMarkdownInput> parsed = WriteMarkdownInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error!));
    }

    Result<string> resolvedPath = parsed.Value!.Path is null
        ? Result.Success("")
        : _resolver.Resolve(parsed.Value.Path);
    if (!resolvedPath.IsSuccess)
    {
      return Task.FromResult(Err(resolvedPath.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error!))
      : ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
        RenderAndMaybeWriteAsync(resolvedPath.Value!, parsed.Value, token), ct);
  }

  private async Task<ToolResult> RenderAndMaybeWriteAsync(string resolvedPath, WriteMarkdownInput args, CancellationToken ct)
  {
    string rendered = MarkdownRenderer.Render(args.Document);

    if (args.Path is null)
    {
      return new ToolResult(rendered, false);
    }

    Result<FileWriteOutcome> written = await _files.WriteFileAsync(resolvedPath, rendered, args.Overwrite!.Value, ct).ConfigureAwait(false);
    if (!written.IsSuccess)
    {
      return Err(written.Error!);
    }

    FileWriteOutcome o = written.Value!;
    return new ToolResult(
        $"[write_markdown {resolvedPath}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
