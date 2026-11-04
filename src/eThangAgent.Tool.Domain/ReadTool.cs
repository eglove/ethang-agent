using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Bounded line-range read with the freshness guard (issue #117): every
///     versioned read's annotation carries the file-version token (B1); a re-read
///     of a range the model already holds at the same version elides its content
///     (B2) — fully or span-by-span — with <c>force</c> as the escape hatch; a
///     changed file re-reads with a different token (B3); a read is never blocked
///     or failed by the guard (B4). The optional ledger is turn-local: the Agent
///     binds one per agent and resets it each turn. A ledger-less tool (legacy
///     wiring) annotates tokens but never elides; a version-less read is
///     byte-identical to the pre-guard output.</summary>
public sealed class ReadTool(IPathResolver resolver, IFileSystemAccess files, ReadFreshnessLedger? ledger = null) : ITool, IWorkspaceScopedTool, ILedgerBoundTool
{
  // The read tool's path policy: relative paths stay bound to the injected
  // resolver's root (containment enforced — '..' cannot escape the workspace or
  // a child's anchor), while absolute paths are accepted from anywhere on disk.
  // Read is read-only, so an absolute path needs no confinement; mutating tools
  // keep the strict resolver. RootedAt re-roots with the same policy over the
  // anchor, so anchored children inherit it unchanged.
  private readonly RootAnchoredPathResolver _resolver =
      new(resolver ?? throw new ArgumentNullException(nameof(resolver)));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly ReadFreshnessLedger? _ledger = ledger;

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new ReadTool(new WorkspacePathResolver(workspaceRoot), _files, _ledger);

  /// <inheritdoc />
  public bool HasLedger => _ledger is not null;

  /// <inheritdoc />
  public ITool WithLedger(ReadFreshnessLedger ledger)
      => _ledger is null ? new ReadTool(_resolver, _files, ledger) : this;

  public ToolDefinition Definition { get; } = new(
      "read",
      "Read a range of lines from a text file. timeoutSeconds, path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call. Freshness: the annotation carries a version token (| v<token>); re-reading a range you already hold returns an elided stub naming the held span instead of the content — pass force=true to get the content anyway.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path: relative paths resolve against the workspace root (and may not leave it); absolute paths may point anywhere on disk."),
            new ToolParameter("startLine", ToolParameterType.WholeNumber, "First line to read (1-based, inclusive).", Minimum: 1),
            new ToolParameter("endLine", ToolParameterType.WholeNumber, "Last line to read (1-based, inclusive).", Minimum: 1),
            new ToolParameter("force", ToolParameterType.Flag,
                "Bypass freshness elision and return the content even when this range is unchanged since your last read."),
      ],
      ["timeoutSeconds", "path", "startLine", "endLine"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ReadToolInput> parsed = ReadToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    ReadToolInput args = parsed.Value;

    Result<string> resolved = _resolver.Resolve(args.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        ReadAsync(args, resolved.Value, token), ct);
  }

  private async Task<ToolResult> ReadAsync(ReadToolInput args, string path, CancellationToken ct)
  {
    Result<FileRead> read = await _files.ReadLinesAsync(path, args.StartLine, args.EndLine, ct).ConfigureAwait(false);
    return read.IsSuccess
      ? HandleRead(args, path, read.Value)
      : Err(read.Error);
  }

  private ToolResult HandleRead(ReadToolInput args, string path, FileRead file)
  {
    if (file.LastLineRead == 0)
    {
      return Err(new DomainError("StartLineBeyondEof",
          $"'startLine' {args.StartLine} exceeds file length ({file.TotalLines} lines)."));
    }

    bool clamped = file.TotalLines < args.EndLine;
    int last = clamped ? file.TotalLines : args.EndLine;

    // Freshness: the verdict keys on the version the ACL captured at the read
    // moment, over the range actually delivered (after clamping).
    FileCoverage coverage = CheckedCoverage(path, file, args.StartLine, last);
    bool forced = args.Force && !coverage.IsEmpty;
    RecordRead(path, file, args.StartLine, last);
    return new ToolResult(ReadOutputRenderer.Render(path, args.StartLine, args.EndLine, file, coverage, forced), false);
  }

  private FileCoverage CheckedCoverage(string path, FileRead file, int start, int last)
      => _ledger?.Check(path, file.Version, start, last) ?? new FileCoverage([]);

  private void RecordRead(string path, FileRead file, int startLine, int last)
  {
    if (_ledger is not null && file.Version is not null)
    {
      _ledger.Record(path, file.Version, startLine, last);
    }
  }

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
