using System.Globalization;
using System.Text;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

public sealed class DbSchemaTool(ISelfDatabaseAccess database) : ITool
{
  private readonly ISelfDatabaseAccess _database = database ?? throw new ArgumentNullException(nameof(database));

  public ToolDefinition Definition { get; } = new(
      "db_schema",
      """
      Inspect the structure of the agent's own app database — the SQLite store behind sessions, transcripts, state keys, memories, skills, and preferences. timeoutSeconds is mandatory; includeCounts is optional (boolean, default false — row counts are opt-in because transcripts can be large). Output begins with an annotation line — metadata, not data: `[db_schema] schema version V, T table(s), I index(es), W view(s)`. Each object then renders as `table NAME` or `view NAME` (with ` (N rows)` appended when includeCounts is true), followed by indented column lines `  name TYPE[ PK][ NOT NULL][ DEFAULT expr]` and indented index lines `  index NAME (col, col)` or `  unique index NAME (col, col)` — the implicit autoindexes that back PRIMARY KEY and UNIQUE constraints are included. Internal sqlite_* tables and FTS5 shadow tables are hidden; query sqlite_master through db_query to see them. Errors begin with `Error [Code]:`.
      """,
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("includeCounts", ToolParameterType.Flag,
              "Optional. true to append ` (N rows)` to each table/view line (default false)."),
      ],
      ["timeoutSeconds"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<DbSchemaToolInput> parsed = DbSchemaToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token =>
        DescribeAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> DescribeAsync(DbSchemaToolInput args, CancellationToken ct)
  {
    Result<SelfDatabaseSchema> described = await _database
        .DescribeAsync(args.IncludeCounts, ct).ConfigureAwait(false);
    return !described.IsSuccess ? Err(described.Error) : Render(described.Value);
  }

  private static ToolResult Render(SelfDatabaseSchema schema)
  {
    int tables = schema.Objects.Count(o => !o.IsView);
    int views = schema.Objects.Count - tables;
    int indexes = schema.Objects.Sum(o => o.Indexes.Count);
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[db_schema] schema version {schema.SchemaVersion}, {tables} table(s), {indexes} index(es), {views} view(s)");
    foreach (SchemaObject obj in schema.Objects)
    {
      string kind = obj.IsView ? "view" : "table";
      string count = obj.RowCount is { } rows ? $" ({rows.ToString(CultureInfo.InvariantCulture)} rows)" : "";
      _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{kind} {obj.Name}{count}");
      foreach (SchemaColumn column in obj.Columns)
      {
        _ = sb.AppendLine(RenderColumn(column));
      }
      foreach (SchemaIndex index in obj.Indexes)
      {
        _ = sb.AppendLine(RenderIndex(index));
      }
    }
    sb.Length -= Environment.NewLine.Length;  // trim trailing newline
    return new ToolResult(sb.ToString(), false);
  }

  private static string RenderColumn(SchemaColumn column)
  {
    StringBuilder line = new("  ");
    _ = line.Append(column.Name);
    if (column.Type.Length > 0)
    {
      _ = line.Append(' ').Append(column.Type);
    }
    if (column.IsPrimaryKey)
    {
      _ = line.Append(" PK");
    }
    if (column.NotNull)
    {
      _ = line.Append(" NOT NULL");
    }
    if (column.DefaultValue is { } declared)
    {
      _ = line.Append(" DEFAULT ").Append(declared);
    }
    return line.ToString();
  }

  private static string RenderIndex(SchemaIndex index) => index.IsUnique
      ? $"  unique index {index.Name} ({string.Join(", ", index.Columns)})"
      : $"  index {index.Name} ({string.Join(", ", index.Columns)})";

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
