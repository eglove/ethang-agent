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
      "Edit a text file in exactly one of three modes (chosen by argument shape; mixing is rejected). " +
      "ANCHOR MODE — timeoutSeconds, path, old, and replacement are mandatory, plus exactly one of " +
      "all (boolean true — replace every occurrence) or occurrences (integer ≥ 1 — expected match count; " +
      "the call fails if the actual count differs, naming both numbers). old must appear verbatim — " +
      "no regex, no whitespace normalization. " +
      "HUNKS MODE — the patch surface for several edits to one file in one call: send hunks, a JSON " +
      "array of objects, each {old, replacement, all|occurrences} under exactly the anchor-mode rules, " +
      "instead of old/replacement at the top level. The hunks are applied ATOMICALLY over ONE file " +
      "read: every hunk lands or NOTHING is applied and the file is left byte-identical, so a partial " +
      "patch cannot exist. Hunks are matched against the file AS IT STANDS, never against another " +
      "hunk's output, so name disjoint regions — two hunks whose matches touch are refused " +
      "(Error [HunksOverlap]) rather than applied in some order. A failure names the hunk that failed, " +
      "as 'hunk N: ...'. An empty replacement deletes the matched text. HUNKS MODE IS PREFERRED over a " +
      "whole-file rewrite (write) and over N sequential anchor calls, which race each other and " +
      "invalidate line numbers. " +
      "RANGE MODE — timeoutSeconds, path, startLine, endLine, " +
      "and replacement are mandatory; lines startLine..endLine (1-based, inclusive, counted exactly as " +
      "read counts them) are replaced by replacement; an empty replacement deletes the range; endLine " +
      "past EOF is rejected (the error names the file's line count) — read the file first for current numbers. " +
      "All modes: the file is never created; " +
      "binary files are refused. LINE ENDINGS ARE NORMALIZED MECHANICALLY: the replacement is rewritten to " +
      "the file's own dominant convention before splicing (untouched lines keep their exact bytes), so an " +
      "edit never introduces a foreign terminator — do not convert line endings yourself in `replacement`. " +
      "Output is annotation lines only. Anchor mode: `[edit <path>] replaced N occurrence(s), file now M lines, " +
      "LF|CRLF line endings`. Hunks mode: one `[edit <path>] hunk K/N: replaced C occurrence(s)` line per " +
      "hunk, then `[edit <path>] N hunk(s) applied, file now M lines, LF|CRLF line endings`. Range mode: " +
      "`[edit <path>] replaced lines N-M, file now M lines, LF|CRLF line endings`. " +
      "Errors begin with " +
      "`Error [Code]:` and are safe to retry with corrected arguments. On AnchorNotFound the error quotes the " +
      "file's nearest matching region when one exists — `Nearest match at line N` plus context lines in the read " +
      "tool's line-number→ gutter format (exact content, exact line numbers) — so correct the anchor from the " +
      "quoted region and retry without re-reading the file. The quoted region is followed by the text that WOULD " +
      "match there, emitted as a ready-to-use 'old': copy that block verbatim as your next `old` and the retry " +
      "succeeds on the first try. The similarity figure is stated only when it explains something — a difference " +
      "that is whitespace-only says so instead of reporting 100%. (The JSON parameter formerly named 'new' is not accepted.)",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
             new ToolParameter("old", ToolParameterType.Text,
                 "Anchor mode: exact text to replace (literal, case-sensitive)."),
             new ToolParameter("replacement", ToolParameterType.Text,
                 "Replacement text (anchor and range modes). May be empty to delete."),
             new ToolParameter("all", ToolParameterType.Flag,
                 "Anchor mode: true to replace every occurrence (mutually exclusive with occurrences)."),
             new ToolParameter("occurrences", ToolParameterType.WholeNumber,
                 "Anchor mode: expected number of replacements (mutually exclusive with all). Minimum: 1", Minimum: 1),
             new ToolParameter("hunks", ToolParameterType.ObjectArray,
                 "Hunks mode: JSON array of {old, replacement, all|occurrences} objects applied atomically. " +
                 "Each entry follows the anchor-mode rules; an empty replacement deletes; at least one hunk."),
             new ToolParameter("startLine", ToolParameterType.WholeNumber,
                 "Range mode: first line to replace (1-based, inclusive). Minimum: 1", Minimum: 1),
             new ToolParameter("endLine", ToolParameterType.WholeNumber,
                 "Range mode: last line to replace (1-based, inclusive; must not exceed file length). Minimum: 1", Minimum: 1),
       ],
       // Exactly one of anchor/range/hunks is a cross-parameter rule the parser enforces.
       ["timeoutSeconds", "path"]);

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
      EditHunksInput hunks => ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
          ReplaceHunksAsync(resolved.Value, hunks, token), ct),
      EditRangeInput range => ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
          ReplaceRangeAsync(resolved.Value, range, token), ct),
      _ => throw new InvalidOperationException("unreachable: edit input has exactly three shapes"),
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
        $"[edit {path}] replaced {o.Replaced} {noun}, file now {o.NewLineCount} lines, " +
        $"{LineEndings.Label(o.NormalizedTo)} line endings",
        false);
  }

  private async Task<ToolResult> ReplaceHunksAsync(string path, EditHunksInput args, CancellationToken ct)
  {
    Result<HunkReplaceOutcome> replaced = await _files.ReplaceHunksAsync(path, args.Hunks, ct).ConfigureAwait(false);
    if (!replaced.IsSuccess)
    {
      return Err(replaced.Error);
    }

    HunkReplaceOutcome o = replaced.Value;
    int total = o.ReplacedPerHunk.Count;
    string[] lines = [..
        o.ReplacedPerHunk.Select((count, index) =>
            $"[edit {path}] hunk {index + 1}/{total}: replaced {count} " +
            (count == 1 ? "occurrence" : "occurrence(s)")),
        $"[edit {path}] {total} {(total == 1 ? "hunk" : "hunks")} applied, file now " +
        $"{o.NewLineCount} lines, {LineEndings.Label(o.NormalizedTo)} line endings",
    ];
    return new ToolResult(string.Join('\n', lines), false);
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
        $"[edit {path}] replaced lines {args.StartLine}-{args.EndLine}, file now {o.NewLineCount} lines, " +
        $"{LineEndings.Label(o.NormalizedTo)} line endings",
        false);
  }

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
