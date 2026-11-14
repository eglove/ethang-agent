using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>What one run of <see cref="ReasoningTraceRetention"/> measured and did: the rows it
///     found and the space returning them to the disk. Every number was taken by a query against
///     the database being swept, never estimated.</summary>
/// <param name="RowsRemoved">Rows this run deleted.</param>
/// <param name="CharsRemoved">The payload text those rows held, in characters.</param>
/// <param name="FirstOccurredAt">The oldest reasoning row the run found, or null when there was none.</param>
/// <param name="LastOccurredAt">The newest reasoning row the run found, or null when there was none.</param>
/// <param name="UsageRowsKept">Rows of type 'usage' left untouched, read so the two populations are told apart.</param>
/// <param name="FileBytesBefore">The database file's size before the sweep.</param>
/// <param name="FileBytesAfterDelete">Its size after the rows were deleted, before the reclaim.</param>
/// <param name="FileBytesAfterReclaim">Its size after the reclaim.</param>
/// <param name="FreePagesBefore">Free pages the file held before the sweep.</param>
/// <param name="FreePagesAfter">Free pages it holds after the reclaim.</param>
/// <param name="ReclaimFailure">Why the reclaim did not finish, or null.</param>
public sealed record ReasoningTraceSweepReport(
    int RowsRemoved,
    long CharsRemoved,
    string? FirstOccurredAt,
    string? LastOccurredAt,
    int UsageRowsKept,
    long FileBytesBefore,
    long FileBytesAfterDelete,
    long FileBytesAfterReclaim,
    long FreePagesBefore,
    long FreePagesAfter,
    string? ReclaimFailure);

/// <summary>The one-time removal of every reasoning trace already persisted (V44).
///     <para>WHY EVERY ROW, NOT A WINDOW: an age-based window can reach none of them. The traces
///     this table holds span 21 days on the machine it was measured on - the whole population sits
///     inside any window a retention policy would name - so a sweep by age would delete nothing
///     while the write that creates the rows kept running. The write is gone (no production caller
///     appends this event), so the rows are a record with no consumer and no producer, and the
///     whole set goes.</para>
///     <para>IT IS NOT A RETENTION POLICY AND DOES NOT TOUCH THE USAGE LEDGER. The neighbouring
///     rows of type 'usage' are the cost ledger three production readers depend on; this deletes
///     by type and nothing else, and the report counts the usage rows it left alone so a reader
///     can see the line was drawn deliberately.</para>
///     <para>THE RECLAIM IS THE LAST THING THAT HAPPENS and it runs outside the delete's
///     transaction, because VACUUM cannot run inside one: a delete whose rows are committed and
///     whose free pages are still in the file has not returned anything to the disk.</para></summary>
public static class ReasoningTraceRetention
{
  /// <summary>This record's version in <see cref="EmbeddedImagePayloadStrip.ReportTable"/>.</summary>
  private const int ReportVersion = 44;

  /// <summary>The event type the traces carry.</summary>
  private const string ReasoningType = "reasoning";

  private const string BusyTimeoutCommand = "PRAGMA busy_timeout = 2000;";

  /// <summary>Deletes every reasoning row this database holds and reclaims the space they
  ///     occupied. Runs inside a migration, so it runs once per database.
  ///     <para>A DATABASE THAT NEVER HELD A TRACE GETS A ZERO RECORD, so 'the sweep ran here and
  ///     found nothing' is a fact a reader can check rather than an absence to interpret.</para>
  ///     <para>A RECLAIM THAT CANNOT RUN IS NOT SWALLOWED: the rows are already gone by then, so
  ///     the version is deliberately left unadvanced by throwing, and the next start finds the
  ///     incomplete record, finds zero rows, and finishes only the reclaim.</para></summary>
  /// <param name="connection">The connection the migration runs on.</param>
  public static ReasoningTraceSweepReport Run(SqliteConnection connection)
  {
    ArgumentNullException.ThrowIfNull(connection);
    Execute(connection, BusyTimeoutCommand);

    long freePagesBefore = Scalar(connection, "PRAGMA freelist_count;");
    long bytesBefore = FileBytes(connection);
    int usageKept = (int)Scalar(connection, "SELECT COUNT(*) FROM agent_events WHERE type = 'usage';");
    (int rows, long chars, string? first, string? last) = Measure(connection);

    long bytesAfterDelete = bytesBefore;
    int removed = 0;
    if (rows > 0)
    {
      using SqliteTransaction transaction = connection.BeginTransaction();
      // The work is named BEFORE it is done: this row commits with the delete, so a sweep that
      // lands leaves its measurement behind rather than a memory of one.
      WriteReport(connection, transaction, new ReasoningTraceSweepReport(0, 0, first, last, usageKept,
          bytesBefore, bytesBefore, bytesBefore, freePagesBefore, freePagesBefore, null));
      removed = DeleteTraces(connection, transaction);
      transaction.Commit();
      bytesAfterDelete = FileBytes(connection);
    }

    string? reclaimFailure = Reclaim(connection);
    if (reclaimFailure is not null)
    {
      throw new InvalidOperationException(
          $"the reasoning traces were removed but the database file could not be reclaimed: {reclaimFailure}. "
          + "The sweep is recorded as incomplete and the next start finishes only the reclaim.");
    }

    long freePagesAfter = Scalar(connection, "PRAGMA freelist_count;");
    ReasoningTraceSweepReport report = new(removed, removed == 0 ? 0 : chars, first, last, usageKept,
        bytesBefore, bytesAfterDelete, FileBytes(connection), freePagesBefore, freePagesAfter, null);
    WriteReport(connection, transaction: null, report);
    return report;
  }

  /// <summary>The report this database holds, or null when no sweep has run here.</summary>
  /// <param name="database">The app database to read.</param>
  /// <param name="ct">Cancellation for the read.</param>
  public static async Task<ReasoningTraceSweepReport?> ReadReportAsync(AppDatabase database,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(database);
#pragma warning disable CA2007
    await using SqliteConnection connection = database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT payload_json FROM migration_reports WHERE migration_version = $version;";
    _ = command.Parameters.AddWithValue("$version", ReportVersion);
    return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string raw
        ? JsonSerializer.Deserialize<ReasoningTraceSweepReport>(raw)
        : null;
  }

  /// <summary>The one pass that names the work: how many traces there are, what they hold, and
  ///     the span they cover.</summary>
  private static (int Rows, long Chars, string? First, string? Last) Measure(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(*),
               COALESCE(SUM(LENGTH(payload_json)), 0),
               MIN(occurred_at),
               MAX(occurred_at)
        FROM agent_events WHERE type = 'reasoning';
        """;
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return (0, 0, null, null);
    }

    return (reader.GetInt32(0), reader.GetInt64(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3));
  }

  /// <summary>Deletes every reasoning row, leaving every other event type in place.</summary>
  private static int DeleteTraces(SqliteConnection connection, SqliteTransaction transaction)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "DELETE FROM agent_events WHERE type = $type;";
    _ = command.Parameters.AddWithValue("$type", ReasoningType);
    return command.ExecuteNonQuery();
  }

  /// <summary>Compacts the file so the space the rows occupied is returned to the disk rather
  ///     than held as free pages. Returns the failure's message, or null on success.</summary>
  private static string? Reclaim(SqliteConnection connection)
  {
    try
    {
      Execute(connection, "VACUUM;");
      return null;
    }
    catch (SqliteException ex)
    {
      return ex.Message;
    }
  }

  private static void WriteReport(SqliteConnection connection, SqliteTransaction? transaction,
      ReasoningTraceSweepReport report)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO migration_reports (migration_version, payload_json, recorded_at)
        VALUES ($version, $payload, $recorded)
        ON CONFLICT(migration_version) DO UPDATE SET payload_json = $payload, recorded_at = $recorded;
        """;
    _ = command.Parameters.AddWithValue("$version", ReportVersion);
    _ = command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(report));
    _ = command.Parameters.AddWithValue("$recorded",
        DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
    _ = command.ExecuteNonQuery();
  }

  /// <summary>The database file's size as the database itself accounts for it: every page the
  ///     file holds, free or not. That is the number a reclaim moves.</summary>
  private static long FileBytes(SqliteConnection connection) =>
      Scalar(connection, "PRAGMA page_count;") * Scalar(connection, "PRAGMA page_size;");

  private static long Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal declared in this class.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static void Execute(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
    _ = command.ExecuteNonQuery();
  }
}
