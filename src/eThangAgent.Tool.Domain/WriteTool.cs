using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class WriteTool(IPathResolver resolver, IFileWriteAccess files) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileWriteAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "write",
      "Create or replace a text file. timeoutSeconds, path, and content are mandatory; " +
      "the call fails if the file exists unless overwrite is exactly true — it will never " +
      "silently replace anything. Omitting overwrite keeps the call create-only. Parent " +
      "directories are never created automatically; create " +
      "them first if needed. Paths resolve inside the workspace; escapes are rejected. Content " +
      "is written verbatim as UTF-8 without BOM (an empty string writes an empty file). Output " +
      "is a single annotation line in [brackets]: `[write <path>] created|overwritten, N bytes` — " +
      "metadata, not file content. Errors begin with `Error [Code]:`.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("content", ToolParameterType.Text,
                "Exact file content. May be empty."),
            new ToolParameter("overwrite", ToolParameterType.Flag,
                "Optional. Defaults to refusing replacement of an existing file; true replaces it."),
      ]);

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
    Result<FileWriteOutcome> written = await _files.WriteFileAsync(path, args.Content, args.Overwrite, ct).ConfigureAwait(false);
    if (!written.IsSuccess)
    {
      return Err(written.Error);
    }

    FileWriteOutcome o = written.Value;
    return new ToolResult(
        $"[write {path}] {(o.Created ? "created" : "overwritten")}, {o.BytesWritten} bytes",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
