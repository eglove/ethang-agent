using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Implements the self-inspection seam over <see cref="AppDatabase"/>. Every
///     statement runs on a read-only connection — SQLite rejects any write at the
///     engine level — so even a statement that slips past the domain's lexical gate
///     cannot mutate the database. This is the only composer of dynamic SQL in the
///     solution; every composed fragment is either a constant, a bound parameter, or
///     an identifier from sqlite_master quoted by doubling embedded quotes.</summary>
public sealed class SqliteSelfDatabaseAccess(AppDatabase database) : ISelfDatabaseAccess
{
  // Writers here run short transactions; a read-only query waits this long for the
  // file lock before "database is locked" surfaces as a QueryFailed error the model
  // can retry against.
  private const string BusyTimeoutCommand = "PRAGMA busy_timeout = 2000;";
  private const string QueryFailedCode = "QueryFailed";

  // FTS5 keeps each index in shadow tables named <base><suffix> where <base> is the
  // external-content table whose name ends in "_fts".
  private static readonly string[] FtsShadowSuffixes = ["_config", "_content", "_data", "_docsize", "_idx"];

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<SelfDatabaseSchema>> DescribeAsync(bool includeCounts, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = await OpenReadOnlyAsync(ct);
#pragma warning restore CA2007
      int version = await ReadSchemaVersionAsync(connection, ct).ConfigureAwait(false);

      List<(string Name, bool IsView)> catalog = [];
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using (SqliteCommand catalogCommand = connection.CreateCommand())
#pragma warning restore CA2007
      {
        catalogCommand.CommandText =
            "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') ORDER BY type, name;";
        using SqliteDataReader reader = await catalogCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          catalog.Add((reader.GetString(0), reader.GetString(1) == "view"));
        }
      }

      List<SchemaObject> objects = [];
      foreach ((string name, bool isView) in catalog.Where(entry => !IsHidden(entry.Name)))
      {
        objects.Add(new SchemaObject(
            name,
            isView,
            includeCounts ? await CountRowsAsync(connection, name, ct).ConfigureAwait(false) : null,
            await DescribeColumnsAsync(connection, name, ct).ConfigureAwait(false),
            await DescribeIndexesAsync(connection, name, ct).ConfigureAwait(false)));
      }

      return Result.Success(new SelfDatabaseSchema(version, objects));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<SelfDatabaseSchema>(new DomainError(QueryFailedCode, ex.Message));
    }
  }

  public async Task<Result<SelfQueryResult>> QueryAsync(string sql, int maxRows, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = await OpenReadOnlyAsync(ct);
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
      // Named decision (CA2100): running the model-supplied read-only query IS the
      // tool's purpose. Enforcement is the read-only connection and the domain's
      // lexical gate, never the command text.
#pragma warning disable CA2007, CA2100 // Review SQL query for security vulnerabilities
      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
#pragma warning restore CA2007, CA2100
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      List<string> columns = [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
      List<IReadOnlyList<SelfQueryCell>> rows = [];
      bool truncated = false;
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        if (rows.Count == maxRows)
        {
          truncated = true;
          break;
        }
        rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => ReadCell(reader, i))]);
      }
      return Result.Success(new SelfQueryResult(columns, rows, truncated));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<SelfQueryResult>(new DomainError(QueryFailedCode, ex.Message));
    }
  }

  /// <summary>Opens the read-only connection and raises its busy timeout so a query
  ///     that lands mid-write waits for the file lock instead of failing instantly.</summary>
  private async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken ct)
  {
    SqliteConnection connection = _database.OpenReadOnly();
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand busy = connection.CreateCommand();
#pragma warning restore CA2007
    busy.CommandText = BusyTimeoutCommand;
    _ = await busy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    return connection;
  }

  private static async Task<int> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
  }

  private static async Task<List<SchemaColumn>> DescribeColumnsAsync(
      SqliteConnection connection, string table, CancellationToken ct)
  {
    List<SchemaColumn> columns = [];
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
    command.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@name);";
    _ = command.Parameters.AddWithValue("@name", table);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    while (await reader.ReadAsync(ct).ConfigureAwait(false))
    {
      columns.Add(new SchemaColumn(
          reader.GetString(0),
          await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? string.Empty : reader.GetString(1),
          reader.GetInt64(2) != 0,
          reader.GetInt64(4) > 0,
          await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetString(3)));
    }
    return columns;
  }

  private static async Task<List<SchemaIndex>> DescribeIndexesAsync(
      SqliteConnection connection, string table, CancellationToken ct)
  {
    List<(string Name, bool Unique)> indexes = [];
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using (SqliteCommand command = connection.CreateCommand())
#pragma warning restore CA2007
    {
      command.CommandText = "SELECT name, \"unique\" FROM pragma_index_list(@name);";
      _ = command.Parameters.AddWithValue("@name", table);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        indexes.Add((reader.GetString(0), reader.GetInt64(1) != 0));
      }
    }

    List<SchemaIndex> described = [];
    foreach ((string name, bool unique) in indexes)
    {
      List<string> columns = [];
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteCommand command = connection.CreateCommand();
#pragma warning restore CA2007
      command.CommandText = "SELECT name FROM pragma_index_info(@name) ORDER BY seqno;";
      _ = command.Parameters.AddWithValue("@name", name);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        columns.Add(reader.GetString(0));
      }
      described.Add(new SchemaIndex(name, unique, columns));
    }
    return described;
  }

  private static async Task<long> CountRowsAsync(SqliteConnection connection, string name, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
    // Named decision (CA2100): the identifier comes from sqlite_master, never from
    // user input, and embedded quotes are doubled by Quote. Identifiers cannot be
    // bound parameters.
#pragma warning disable CA2007, CA2100 // Review SQL query for security vulnerabilities
    await using SqliteCommand command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM {Quote(name)};";
#pragma warning restore CA2007, CA2100
    return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
  }

  private static SelfQueryCell ReadCell(SqliteDataReader reader, int ordinal)
  {
    object value = reader.GetValue(ordinal);
    return value switch
    {
      DBNull or null => SelfQueryCell.Null,
      byte[] blob => new SelfQueryCell(null, blob.Length),
      string text => new SelfQueryCell(text, null),
      IFormattable formattable => new SelfQueryCell(
          Convert.ToString(formattable, CultureInfo.InvariantCulture), null),
      _ => new SelfQueryCell(value.ToString(), null),
    };
  }

  private static string Quote(string identifier) =>
      $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

  /// <summary>Internal sqlite_* tables (including sqlite_sequence) and the FTS5 shadow
  ///     tables that mirror content already stored in their base tables are hidden
  ///     from the schema report. They remain fully queryable through db_query.</summary>
  private static bool IsHidden(string name)
  {
    if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }
    foreach (string suffix in FtsShadowSuffixes)
    {
      if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
          && name[..^suffix.Length].EndsWith("_fts", StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }
    return false;
  }
}
