using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class DbQueryTool(ISelfDatabaseAccess database) : ITool
{
  private readonly ISelfDatabaseAccess _database = database ?? throw new ArgumentNullException(nameof(database));

  public ToolDefinition Definition { get; } = new(
      "db_query",
      """
      Run ONE read-only SQL query (SELECT or WITH) against the agent's own app database — the SQLite store behind sessions, transcripts, state keys, memories, skills, and preferences. timeoutSeconds and sql are mandatory; maxRows is optional (integer 1..1000, default 100). Only a single statement beginning with SELECT or WITH is accepted: multiple statements, ATTACH/DETACH, and pragmas fail with Error [InvalidSql], and any write attempt fails because the connection is read-only (Error [QueryFailed]). Output begins with an annotation line — metadata, not data: `[db_query] N row(s) shown, M column(s)` (plus `; result set truncated — add or raise LIMIT` when more rows existed). Then the header row, a gutter of five dashes per column joined with '+', and one line per row, cells joined with ' | '. In-cell escapes: backslash to \\, pipe to \|, newline to \n, carriage return to \r, tab to \t. SQL NULL renders as <null>; BLOB renders as <blob N bytes>. Errors begin with `Error [Code]:` and are safe to retry with corrected sql.
      """,
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("sql", ToolParameterType.Text,
              "One read-only SQL statement (SELECT or WITH) against the app database. Run db_schema first to learn the structure."),
          new ToolParameter("maxRows", ToolParameterType.WholeNumber,
              "Optional row cap, 1..1000 (default 100). More rows set the truncated marker in the annotation line.", Minimum: 1),
      ],
      ["timeoutSeconds", "sql"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<DbQueryToolInput> parsed = DbQueryToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
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
        QueryAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> QueryAsync(DbQueryToolInput args, CancellationToken ct)
  {
    Result<SelfQueryResult> queried = await _database
        .QueryAsync(args.Sql, args.MaxRows, ct).ConfigureAwait(false);
    return !queried.IsSuccess ? Err(queried.Error) : Render(queried.Value);
  }

  private static ToolResult Render(SelfQueryResult result)
  {
    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture,
        $"[db_query] {result.Rows.Count} row(s) shown, {result.Columns.Count} column(s)");
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
      .Replace("\r", "\\r", StringComparison.Ordinal)
      .Replace("\n", "\\n", StringComparison.Ordinal)
      .Replace("\t", "\\t", StringComparison.Ordinal);

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
