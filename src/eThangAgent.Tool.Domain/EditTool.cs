using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class EditTool(IPathResolver resolver, IFileEditAccess files) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileEditAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "edit",
      "Edit a text file by exact literal replacement. timeoutSeconds, path, old, and new are mandatory; then provide " +
      "exactly one of all (boolean true — replace every occurrence) or occurrences (integer ≥ 1 — expected " +
      "match count; the call fails if the actual count differs, naming both numbers). old must appear verbatim — " +
      "no regex, no whitespace normalization. An empty new deletes the matched text. The file is never created. " +
      "Binary files are refused. Output is a single annotation line: `[edit <path>] replaced N occurrence(s), " +
      "file now M lines`. Errors begin with `Error [Code]:` and are safe to retry with corrected arguments.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("old", ToolParameterType.Text,
                "Exact text to replace (literal, case-sensitive)."),
            new ToolParameter("new", ToolParameterType.Text,
                "Replacement text. May be empty to delete."),
            new ToolParameter("all", ToolParameterType.Flag,
                "true to replace every occurrence (mutually exclusive with occurrences)."),
            new ToolParameter("occurrences", ToolParameterType.WholeNumber,
                "Expected number of replacements (mutually exclusive with all). Minimum: 1", Minimum: 1),
      ],
      ["timeoutSeconds", "path", "old", "new"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<EditToolInput> parsed = EditToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error!));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value!.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error!));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    if (!budget.IsSuccess)
    {
      return Task.FromResult(Err(budget.Error!));
    }

    EditToolInput v = parsed.Value;
    return ToolExecution.RunAsync(input.Name, budget.Value!.Timeout, token =>
        ReplaceAsync(resolved.Value!, v, token), ct);
  }

  private async Task<ToolResult> ReplaceAsync(string path, EditToolInput args, CancellationToken ct)
  {
    Result<ReplaceOutcome> replaced = await _files.ReplaceInFileAsync(
        path, args.Old, args.New, args.All ? null : args.Occurrences, ct).ConfigureAwait(false);
    if (!replaced.IsSuccess)
    {
      return Err(replaced.Error);
    }

    ReplaceOutcome o = replaced.Value;
    string noun = o.Replaced == 1 ? "occurrence" : "occurrence(s)";
    return new ToolResult(
        $"[edit {path}] replaced {o.Replaced} {noun}, file now {o.NewLineCount} lines",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
