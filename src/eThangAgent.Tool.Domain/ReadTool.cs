using System.Globalization;
using System.Text;
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
public sealed class ReadTool(IPathResolver resolver, IFileSystemAccess files, ReadFreshnessLedger? ledger = null) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly IFileSystemAccess _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly ReadFreshnessLedger? _ledger = ledger;

  /// <inheritdoc />
  public ITool RootedAt(string workspaceRoot)
      => new ReadTool(new WorkspacePathResolver(workspaceRoot), _files, _ledger);

  public ToolDefinition Definition { get; } = new(
      "read",
      "Read a range of lines from a text file. timeoutSeconds, path, startLine, and endLine are all mandatory; line numbers are 1-based and inclusive. Output begins with an annotation line in [brackets] — it is metadata, not file content. Each content line is prefixed with its line number and →; the number and arrow are never part of the file. Never reproduce line numbers or arrows when creating or editing files. Cite line numbers as shown when referencing locations. If endLine exceeds the file length it is clamped and a [warning] is appended. Maximum range: 1000 lines per call. Freshness: the annotation carries a version token (| v<token>); re-reading a range you already hold returns an elided stub naming the held span instead of the content — pass force=true to get the content anyway.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
            new ToolParameter("path", ToolParameterType.Text,
                "File path, workspace-relative or absolute-inside-workspace."),
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
    if (!read.IsSuccess)
    {
      return Err(read.Error);
    }

    FileRead file = read.Value;
    if (file.LastLineRead == 0)
    {
      return Err(new DomainError("StartLineBeyondEof",
          $"'startLine' {args.StartLine} exceeds file length ({file.TotalLines} lines)."));
    }

    bool clamped = file.TotalLines < args.EndLine;
    int last = clamped ? file.TotalLines : args.EndLine;

    // Freshness: the verdict keys on the version the ACL captured at the read
    // moment, over the range actually delivered (after clamping).
    FileCoverage coverage = _ledger?.Check(path, file.Version, args.StartLine, last)
        ?? new FileCoverage([]);
    bool forced = args.Force && !coverage.IsEmpty;
    if (_ledger is not null && file.Version is not null)
    {
      _ledger.Record(path, file.Version, args.StartLine, last);
    }

    string tokenSuffix = file.Version is null ? string.Empty : $" | {file.Version.Token}";
    string elisionSuffix = ElisionSuffix(coverage, forced, args.StartLine, last);

    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[read {path} lines {args.StartLine}-{last} of {file.TotalLines} total{tokenSuffix}{elisionSuffix}]");

    if (!forced && !coverage.IsEmpty)
    {
      // Elide: deliver only the lines the model does NOT already hold.
      AppendUncoveredLines(sb, file.Lines, args.StartLine, coverage.Elided, width: last.ToString(CultureInfo.InvariantCulture).Length);
    }
    else
    {
      AppendAllLines(sb, file.Lines, args.StartLine, width: last.ToString(CultureInfo.InvariantCulture).Length);
    }

    if (clamped)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"[warning] endLine {args.EndLine} exceeded file length ({file.TotalLines}); clamped");
    }
    else
    {
      sb.Length -= Environment.NewLine.Length;  // trim trailing newline
    }

    return new ToolResult(sb.ToString(), false);
  }

  /// <summary>Appends the lines of the delivered slice that fall OUTSIDE the held
  ///     spans, keeping their absolute line numbers.</summary>
  private static void AppendUncoveredLines(StringBuilder sb, IReadOnlyList<string> lines, int firstLine,
      IReadOnlyList<LineSpan> elided, int width)
  {
    int line = firstLine;
    int spanIdx = 0;
    foreach (string text in lines)
    {
      while (spanIdx < elided.Count && line > elided[spanIdx].End)
      {
        spanIdx++;
      }

      bool held = spanIdx < elided.Count && line >= elided[spanIdx].Start;
      if (!held)
      {
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{line.ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {text}");
      }

      line++;
    }
  }

  private static void AppendAllLines(StringBuilder sb, IReadOnlyList<string> lines, int firstLine, int width)
  {
    foreach ((string? text, int i) in lines.Select((t, i) => (t, i)))
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{(firstLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {text}");
    }
  }

  /// <summary>The annotation's elision suffix: empty when nothing is held, 'forced'
  ///     when the model overrode, a whole-range elision line for full coverage, or a
  ///     span list naming exactly which ranges the model already holds.</summary>
  private static string ElisionSuffix(FileCoverage coverage, bool forced, int startLine, int last)
  {
    if (coverage.IsEmpty)
    {
      return string.Empty;
    }

    if (forced)
    {
      return " | forced";
    }

    bool fullyCovered = coverage.Elided[0].Start <= startLine && coverage.Elided[^1].End >= last;
    return fullyCovered
        ? " | unchanged since your last read; content elided"
        : $" | lines {DescribeSpans(coverage.Elided)} elided (unchanged since your last read)";
  }

  /// <summary>"lines 2-3, 7" — the elided spans, comma-separated.</summary>
  private static string DescribeSpans(IReadOnlyList<LineSpan> spans)
      => string.Join(", ", spans.Select(s => s.ToString()));

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
