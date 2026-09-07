using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Grand-plan general SQLite inspection: read-only SQL over any SQLite file
///     inside the workspace (db_query covers only the agent's own app database). Same
///     lexical gate as db_query, same pipe-table output contract under a
///     <c>[sqlite_query resolved-path]</c> annotation that names the file. The path is
///     resolved and refused outside the workspace before the seam is touched; the
///     seam's read-only connection is the enforcement backstop.</summary>
public sealed class SqliteQueryTool(IPathResolver resolver, ISqliteFileAccess files) : ITool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly ISqliteFileAccess _files = files ?? throw new ArgumentNullException(nameof(files));

  public ToolDefinition Definition { get; } = new(
      "sqlite_query",
      """
      Run ONE read-only SQL query (SELECT or WITH) against an arbitrary SQLite file inside the workspace — inventory databases, exports, or any other .db/.sqlite file. timeoutSeconds, path, and sql are mandatory; maxRows is optional (integer 1..1000, default 100). The path must resolve inside the workspace (Error [PathOutsideWorkspace] otherwise); only a single statement beginning with SELECT or WITH is accepted (Error [InvalidSql] otherwise), and any write attempt fails because the connection is read-only (Error [QueryFailed]). Output begins with an annotation line — metadata, not data: `[sqlite_query resolved-path] N row(s) shown, M column(s)` (plus `; result set truncated — add or raise LIMIT` when more rows existed). Then the header row, a gutter of five dashes per column joined with '+', and one line per row, cells joined with ' | '. In-cell escapes: backslash to \\, pipe to \|, newline to \n, carriage return to \r, tab to \t. SQL NULL renders as <null>; BLOB renders as <blob N bytes>. A missing or non-SQLite file fails Error [QueryFailed]. Errors begin with `Error [Code]:` and are safe to retry with corrected input.
      """,
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("path", ToolParameterType.Text,
              "SQLite file path, workspace-relative or absolute-inside-workspace."),
          new ToolParameter("sql", ToolParameterType.Text,
              "One read-only SQL statement (SELECT or WITH) against that file."),
          new ToolParameter("maxRows", ToolParameterType.WholeNumber,
              "Optional row cap, 1..1000 (default 100). More rows set the truncated marker in the annotation line.", Minimum: 1),
      ],
      ["timeoutSeconds", "path", "sql"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<SqliteQueryToolInput> parsed = SqliteQueryToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<string> resolved = _resolver.Resolve(parsed.Value.Path);
    if (!resolved.IsSuccess)
    {
      return Task.FromResult(Err(resolved.Error));
    }

    DomainError? invalid = ReadOnlySqlValidator.Validate(parsed.Value.Sql);
    if (invalid is not null)
    {
      return Task.FromResult(Err(invalid));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        QueryAsync(parsed.Value, resolved.Value, token), ct);
  }

  private async Task<ToolResult> QueryAsync(SqliteQueryToolInput args, string path, CancellationToken ct)
  {
    Result<SelfQueryResult> queried = await _files
        .QueryAsync(path, args.Sql, args.MaxRows, ct).ConfigureAwait(false);
    return !queried.IsSuccess ? Err(queried.Error) : Render(path, queried.Value);
  }

  private static ToolResult Render(string path, SelfQueryResult result)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[sqlite_query {path}] {result.Rows.Count} row(s) shown, {result.Columns.Count} column(s)");
    if (result.Truncated)
    {
      _ = sb.Append("; result set truncated — add or raise LIMIT");
    }
    _ = sb.AppendLine();
    _ = sb.AppendLine(RenderRow([.. result.Columns.Select(c => new SelfQueryCell(c, null))]));
    _ = sb.AppendLine(Gutter(result.Columns.Count));
    foreach (IReadOnlyList<SelfQueryCell> row in result.Rows)
    {
      _ = sb.AppendLine(RenderRow(row));
    }
    sb.Length -= Environment.NewLine.Length;  // trim trailing newline
    return new ToolResult(sb.ToString(), false);
  }

  private static string RenderRow(IReadOnlyList<SelfQueryCell> cells) =>
      string.Join(" | ", [.. cells.Select(RenderCell)]);

  private static string RenderCell(SelfQueryCell cell) =>
      cell switch
      {
        { BlobByteCount: { } bytes } => string.Create(CultureInfo.InvariantCulture, $"<blob {bytes} bytes>"),
        { Text: { } text } => Escape(text),
        _ => "<null>",
      };

  private static string Gutter(int columns) =>
      string.Join("+", Enumerable.Repeat("-----", columns));

  private static string Escape(string text) => text
      .Replace("\\", "\\\\", StringComparison.Ordinal)
      .Replace("|", "\\|", StringComparison.Ordinal)
      .Replace("\n", "\\n", StringComparison.Ordinal)
      .Replace("\r", "\\r", StringComparison.Ordinal)
      .Replace("\t", "\\t", StringComparison.Ordinal);

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
