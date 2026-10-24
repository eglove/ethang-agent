using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteTool(IPathResolver resolver, IFileWriteAccess files) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileWriteAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new WriteTool(new WorkspacePathResolver(workspaceRoot), _files);

  public ToolDefinition Definition { get; } = new(
      "write",
      "Create or replace a text file. timeoutSeconds and path are mandatory; then provide " +
      "exactly one of content (string) or lines (array of strings, one element per line; an empty " +
      "array writes an empty file; empty elements are blank lines). 'lines' " +
      "avoids raw-string escaping hazards: each element is carried verbatim, so content " +
      "that itself contains raw-string delimiters belongs here rather than built inside " +
      "an exec script. The call fails if " +
      "the file exists unless overwrite is exactly true — it will never silently replace anything. " +
      "Omitting overwrite keeps the call create-only. Parent directories are never created " +
      "automatically; the DirectoryNotFound error names the missing directory and the remedy. " +
      "Paths resolve inside the workspace; escapes are rejected. Content is written as " +
      "UTF-8 without BOM (empty content writes an empty file). LINE ENDINGS ARE NORMALIZED " +
      "MECHANICALLY: the text is rewritten to the target file's own dominant convention (an " +
      "existing file is never restyled), or for a new file to the project's convention resolved " +
      "from .editorconfig end_of_line, then .gitattributes eol=, then the workspace's existing " +
      "majority, then LF. Do not convert line endings yourself — no scripts that rewrite files to " +
      "fix EOLs; they already conform. Output is a single annotation line in [brackets]: " +
      "`[write <path>] created|overwritten, N bytes, LF|CRLF line endings` — metadata, not file " +
      "content. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("content", ToolParameterType.Text,
                "Exact file content. May be empty. Exactly one of content or lines."),
            new ToolParameter("lines", ToolParameterType.TextArray,
                "File content as one string per line, joined with the target file's line-ending convention (the project's for a new file). Empty elements are blank lines; an empty array writes an empty file. Exactly one of content or lines."),
            new ToolParameter("overwrite", ToolParameterType.Flag,
                "Optional. Defaults to refusing replacement of an existing file; true replaces it."),
      ],
      ["timeoutSeconds", "path"]);  // exactly one of content/lines is a cross-parameter rule the parser enforces

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<WriteToolInput> parsed = WriteToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        WriteAsync(resolved.Value, parsed.Value, token), ct);
  }

  private async Task<ToolResult> WriteAsync(string path, WriteToolInput args, CancellationToken ct)
  {
    // 'lines' mode goes through its own seam method so the backend joins with the
    // file's resolved line-ending convention instead of a hardcoded LF.
    // WriteToolInput guarantees exactly one of Lines/Content is set, so the null-forgiving
    // operator below is the parser's contract restated at its only consumer.
    Result<FileWriteOutcome> written = args.Lines is { } lines
        ? await _files.WriteFileLinesAsync(path, lines, args.Overwrite, ct).ConfigureAwait(false)
        : await _files.WriteFileAsync(path, args.Content!, args.Overwrite, ct).ConfigureAwait(false);
    if (!written.IsSuccess)
    {
      return Err(written.Error);
    }

    FileWriteOutcome o = written.Value;
    return new ToolResult(
        $"[write {path}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes, " +
        $"{LineEndings.Label(o.NormalizedTo)} line endings",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
