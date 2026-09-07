using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Implements the arbitrary-file inspection seam: opens the caller-supplied
///     SQLite file in <see cref="SqliteOpenMode.ReadOnly"/> mode, so a missing file is
///     never created and every write fails at the engine. Result shapes and cell
///     reading are shared vocabulary with <see cref="SqliteSelfDatabaseAccess"/>.</summary>
public sealed class SqliteFileAccess : ISqliteFileAccess
{
  private const string BusyTimeoutCommand = "PRAGMA busy_timeout = 2000;";
  private const string QueryFailedCode = "QueryFailed";

  public async Task<Result<SelfQueryResult>> QueryAsync(string path, string sql, int maxRows, CancellationToken ct = default)
  {
    try
    {
      SqliteConnectionStringBuilder builder = new() { DataSource = path, Mode = SqliteOpenMode.ReadOnly };
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = new(builder.ToString());
      await connection.OpenAsync(ct).ConfigureAwait(false);
      await using SqliteCommand busy = connection.CreateCommand();
      busy.CommandText = BusyTimeoutCommand;
      _ = await busy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

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
#pragma warning restore CA2007
    }
    catch (SqliteException ex)
    {
      return Result.Failure<SelfQueryResult>(new DomainError(QueryFailedCode, ex.Message));
    }
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
}
