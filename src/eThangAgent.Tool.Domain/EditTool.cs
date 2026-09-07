using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class EditTool(IPathResolver resolver, IFileEditAccess files) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileEditAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new EditTool(new WorkspacePathResolver(workspaceRoot), _files);

  public ToolDefinition Definition { get; } = new(
      "edit",
      "Edit a text file in exactly one of two modes (chosen by argument shape; mixing is rejected). " +
      "ANCHOR MODE — timeoutSeconds, path, old, and replacement are mandatory, plus exactly one of " +
      "all (boolean true — replace every occurrence) or occurrences (integer ≥ 1 — expected match count; " +
      "the call fails if the actual count differs, naming both numbers). old must appear verbatim — " +
      "no regex, no whitespace normalization. RANGE MODE — timeoutSeconds, path, startLine, endLine, " +
      "and replacement are mandatory; lines startLine..endLine (1-based, inclusive, counted exactly as " +
      "read counts them) are replaced by replacement; an empty replacement deletes the range; endLine " +
      "past EOF is rejected (the error names the file's line count) — read the file first for current numbers. " +
      "Both modes: replacement may be empty in anchor mode (deletes matched text); the file is never created; " +
      "binary files are refused. Output is a single annotation line: `[edit <path>] replaced N occurrence(s), " +
      "file now M lines` or `[edit <path>] replaced lines N-M, file now M lines`. Errors begin with " +
      "`Error [Code]:` and are safe to retry with corrected arguments. (The JSON parameter formerly named " +
      "'new' is not accepted.)",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
            new ToolParameter("old", ToolParameterType.Text,
                "Anchor mode: exact text to replace (literal, case-sensitive)."),
            new ToolParameter("replacement", ToolParameterType.Text,
                "Replacement text (both modes). May be empty to delete."),
            new ToolParameter("all", ToolParameterType.Flag,
                "Anchor mode: true to replace every occurrence (mutually exclusive with occurrences)."),
            new ToolParameter("occurrences", ToolParameterType.WholeNumber,
                "Anchor mode: expected number of replacements (mutually exclusive with all). Minimum: 1", Minimum: 1),
            new ToolParameter("startLine", ToolParameterType.WholeNumber,
                "Range mode: first line to replace (1-based, inclusive). Minimum: 1", Minimum: 1),
            new ToolParameter("endLine", ToolParameterType.WholeNumber,
                "Range mode: last line to replace (1-based, inclusive; must not exceed file length). Minimum: 1", Minimum: 1),
      ],
      ["timeoutSeconds", "path", "old", "replacement"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<EditToolInput> parsed = EditToolInput.Create(input.JsonArguments);
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
    if (!budget.IsSuccess)
    {
      return Task.FromResult(Err(budget.Error));
    }

    EditToolInput v = parsed.Value;
    return v switch
    {
      EditAnchorInput anchor => ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
          ReplaceAsync(resolved.Value, anchor, token), ct),
      EditRangeInput range => ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
          ReplaceRangeAsync(resolved.Value, range, token), ct),
      _ => throw new InvalidOperationException("unreachable: edit input has exactly two shapes"),
    };
  }

  private async Task<ToolResult> ReplaceAsync(string path, EditAnchorInput args, CancellationToken ct)
  {
    Result<ReplaceOutcome> replaced = await _files.ReplaceInFileAsync(
        path, args.Old, args.Replacement, args.All ? null : args.Occurrences, ct).ConfigureAwait(false);
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

  private async Task<ToolResult> ReplaceRangeAsync(string path, EditRangeInput args, CancellationToken ct)
  {
    Result<ReplaceOutcome> replaced = await _files.ReplaceLineRangeAsync(
        path, args.StartLine, args.EndLine, args.Replacement, ct).ConfigureAwait(false);
    if (!replaced.IsSuccess)
    {
      return Err(replaced.Error);
    }

    ReplaceOutcome o = replaced.Value;
    return new ToolResult(
        $"[edit {path}] replaced lines {args.StartLine}-{args.EndLine}, file now {o.NewLineCount} lines",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
