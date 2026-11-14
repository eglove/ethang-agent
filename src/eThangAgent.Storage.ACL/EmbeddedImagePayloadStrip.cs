using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>What one run of <see cref="EmbeddedImagePayloadStrip"/> measured and did. Every number
///     here was taken by a query against the database being stripped, never estimated.
///     <para><see cref="Completed"/> is false only for a run that stripped its rows and then could
///     not reclaim the file: the rows are gone, the file still holds their free pages, and the
///     next start finishes the job. A record left in that state is the audit trail of a one-way
///     operation that is half done, which is exactly what a reader needs to know.</para></summary>
/// <param name="Completed">True when the strip and its reclaim both finished.</param>
/// <param name="RowsCarryingPayloads">Rows that held at least one embedded base64 payload.</param>
/// <param name="PayloadEntries">Embedded payloads across those rows (a row can hold several).</param>
/// <param name="Base64Chars">The payload text's total length, which is what the rows held.</param>
/// <param name="DecodedBytes">The same payloads' decoded size, which is what was actually lost.</param>
/// <param name="RowsSkippedUnreadable">Rows that look like payloads but whose meta_json is not JSON.</param>
/// <param name="RowsStripped">Rows this run rewrote.</param>
/// <param name="FileBytesBefore">The database file's size before the strip.</param>
/// <param name="FileBytesAfterStrip">Its size after the rows were rewritten, before the reclaim.</param>
/// <param name="FileBytesAfterReclaim">Its size after the reclaim.</param>
/// <param name="FreePagesBefore">Free pages the file held before the strip.</param>
/// <param name="FreePagesAfter">Free pages it holds after the reclaim.</param>
/// <param name="ReclaimFailure">Why the reclaim did not finish, or null.</param>
public sealed record EmbeddedImageStripReport(
    bool Completed,
    int RowsCarryingPayloads,
    int PayloadEntries,
    long Base64Chars,
    long DecodedBytes,
    int RowsSkippedUnreadable,
    int RowsStripped,
    long FileBytesBefore,
    long FileBytesAfterStrip,
    long FileBytesAfterReclaim,
    long FreePagesBefore,
    long FreePagesAfter,
    string? ReclaimFailure);

/// <summary>The one-time strip of the image bytes history already holds (V43).
///     <para>WHY IT IS A NAMED CLASS AND NOT A MIGRATION BODY: a JSON rewrite over thousands of
///     rows and the reclaim that follows it are two different risk profiles. The rewrite commits
///     in one transaction so a half-stripped transcript never exists; the reclaim runs AFTER that
///     transaction, because VACUUM cannot run inside one. Keeping the two here, with the report
///     they produce, is what makes the ordering reviewable instead of buried in a migration.</para>
///     <para>THE CANDIDATE SET IS THE JSON STRUCTURE, NEVER A TEXT MATCH. Live databases hold rows
///     whose tool-call ARGUMENTS quote the string 'Base64Data' - an exec program that wrote about
///     this very change - and a LIKE-driven strip would rewrite that history. A row is a candidate
///     only when it is valid JSON, its Parts is an array, and an entry in it carries a Base64Data
///     value.</para>
///     <para>THE STRIP IS ON PRESENCE, NOT SIZE. 2,088 of the 2,211 payload rows measured on the
///     real database are under 1 MB and together hold 608 MB of the 893 MB, so a threshold would
///     leave most of the weight behind. Every embedded payload goes, however small.</para>
///     <para>MESSAGE TEXT IS NOT TOUCHED. The content and role columns are read by nothing here;
///     what changes is the payload's place in meta_json, which keeps the media type and gains the
///     decoded size of what was removed.</para></summary>
public static class EmbeddedImagePayloadStrip
{
  /// <summary>The table the record lives in, created by V43 itself. The record is NOT a preference:
  ///     <c>app_preferences</c> is a key-value store for what the app and its user chose, and a
  ///     migration's audit row is neither. Keeping it in its own table also means the question
  ///     'does the table exist yet' has one answer - this step creates it - rather than depending
  ///     on how old the database is.</summary>
  public const string ReportTable = "migration_reports";

  /// <summary>The version this record belongs to.</summary>
  private const int ReportVersion = 43;

  /// <summary>How long a lock wait lasts before the strip gives up. The same 2 s every other
  ///     store in this assembly waits: this assembly's contract is that a busy database is
  ///     reported, not that a caller blocks on it indefinitely.</summary>
  private const string BusyTimeoutCommand = "PRAGMA busy_timeout = 2000;";

  /// <summary>Every row holding at least one embedded payload, with its totals - the work named
  ///     before it is done. One pass over the table: the payload text is measured by LENGTH and
  ///     its decoded size by the base64 rule, so the numbers are the database's own.
  ///     <para>THE json_type GUARD keeps a Parts key that is not an array out of json_each, and
  ///     the json_valid guard keeps an unreadable row out entirely - those are counted by
  ///     <see cref="UnreadableSql"/> instead and left alone.</para></summary>
  private const string CandidateSql = """
      SELECT rowid, COUNT(*) AS entries,
             COALESCE(SUM(LENGTH(payload)), 0) AS chars,
             COALESCE(SUM(LENGTH(payload) * 3 / 4
                 - CASE WHEN substr(payload, -2) = '==' THEN 2
                        WHEN substr(payload, -1) = '=' THEN 1
                        ELSE 0 END), 0) AS decoded
      FROM (
        SELECT m.rowid AS rowid,
               json_extract(CASE WHEN p.type = 'object' THEN p.value ELSE '{}' END,
                   '$.Base64Data') AS payload
        FROM agent_messages m,
             json_each(CASE WHEN json_valid(m.meta_json) THEN m.meta_json ELSE '[]' END, '$.Parts') p
        WHERE json_type(CASE WHEN json_valid(m.meta_json) THEN m.meta_json ELSE 'null' END,
                        '$.Parts') = 'array'
      )
      WHERE payload IS NOT NULL
      GROUP BY rowid
      ORDER BY rowid;
      """;

  /// <summary>Rows that look like they hold a payload but whose meta_json will not parse. They are
  ///     counted and left alone: a row nothing can read is not a row this strip may guess at, and
  ///     crashing the migration over one would stop the app from starting.</summary>
  private const string UnreadableSql = """
      SELECT COUNT(*) FROM agent_messages
      WHERE meta_json IS NOT NULL
        AND meta_json LIKE '%Base64Data%'
        AND NOT json_valid(meta_json);
      """;

  /// <summary>Strips every embedded payload this database holds and reclaims the space they
  ///     occupied. Runs inside a migration, so it runs once per database.
  ///     <para>A DATABASE ALREADY STRIPPED IS NOT STRIPPED AGAIN. When nothing is left to strip and a
  ///     completed record is already here, this run changes no row and returns a zero report WITHOUT
  ///     overwriting that record - the audit trail of the strip that did the work is the one worth
  ///     keeping, and a later zero would erase the only account of what was removed. A database that
  ///     never held a payload gets its zero record written, so 'the strip ran here and found nothing'
  ///     is a fact a reader can check rather than an absence they have to interpret.</para>
  ///     <para>A RECLAIM THAT CANNOT RUN IS NOT SWALLOWED. The rows are already gone by then, so
  ///     the version is deliberately left unadvanced by throwing: the next start finds the
  ///     incomplete record, measures zero rows, and finishes only the reclaim. A record claiming
  ///     success over a file that still holds the free pages would be the one lie this whole
  ///     operation exists to avoid.</para></summary>
  /// <param name="connection">The connection the migration runs on.</param>
  public static EmbeddedImageStripReport Run(SqliteConnection connection)
  {
    ArgumentNullException.ThrowIfNull(connection);
    Execute(connection, BusyTimeoutCommand);

    long freePagesBefore = Scalar(connection, "PRAGMA freelist_count;");
    long bytesBefore = FileBytes(connection);
    EmbeddedImageStripReport? prior = ReadReport(connection);
    (int rows, int entries, long chars, long decoded, List<long> rowids) = Measure(connection);
    int skipped = (int)Scalar(connection, UnreadableSql);

    if (rows == 0 && prior is { Completed: true })
    {
      // ALREADY STRIPPED: this run changes no row and reports zero, and it deliberately does not
      // overwrite the record - the audit trail of the strip that did the work is the one worth
      // keeping, and a later zero would erase the only account of what was removed.
      return new EmbeddedImageStripReport(true, 0, 0, 0, 0, skipped, 0,
          bytesBefore, bytesBefore, bytesBefore, freePagesBefore, freePagesBefore, null);
    }

    if (rows == 0 && prior is null)
    {
      // A database that never held an embedded payload - the ordinary fresh install. The record
      // still lands, saying zero, so 'the strip ran here and found nothing' is a fact a reader can
      // check rather than an absence they have to interpret.
      EmbeddedImageStripReport empty = new(true, 0, 0, 0, 0, skipped, 0,
          bytesBefore, bytesBefore, bytesBefore, freePagesBefore, freePagesBefore, null);
      WriteReport(connection, transaction: null, empty);
      return empty;
    }

    int stripped = 0;
    long bytesAfterStrip = bytesBefore;
    if (rows > 0)
    {
      using SqliteTransaction transaction = connection.BeginTransaction();
      // Behaviour 1: the work is named BEFORE it is done. This row commits with the rewrite, so
      // a strip that lands leaves its measurement behind rather than a memory of one.
      WriteReport(connection, transaction, new EmbeddedImageStripReport(false, rows, entries,
          chars, decoded, skipped, 0, bytesBefore, bytesBefore, bytesBefore, freePagesBefore,
          freePagesBefore, null));
      stripped = StripRows(connection, transaction, rowids);
      transaction.Commit();
      bytesAfterStrip = FileBytes(connection);
    }

    string? reclaimFailure = Reclaim(connection);
    if (reclaimFailure is not null)
    {
      throw new InvalidOperationException(
          $"the embedded image payloads were stripped but the database file could not be reclaimed: {reclaimFailure}. "
          + "The strip is recorded as incomplete and the next start finishes only the reclaim.");
    }

    long bytesAfterReclaim = FileBytes(connection);
    long freePagesAfter = Scalar(connection, "PRAGMA freelist_count;");
    EmbeddedImageStripReport report;
    if (rows > 0)
    {
      report = new EmbeddedImageStripReport(true, rows, entries, chars, decoded, skipped, stripped,
          bytesBefore, bytesAfterStrip, bytesAfterReclaim, freePagesBefore, freePagesAfter, null);
    }
    else
    {
      // The reclaim-only retry keeps the measurement the strip that did the work recorded.
      report = prior! with
      {
        Completed = true,
        FileBytesAfterStrip = bytesBefore,
        FileBytesAfterReclaim = bytesAfterReclaim,
        FreePagesAfter = freePagesAfter,
      };
    }

    WriteReport(connection, transaction: null, report);
    return report;
  }

  /// <summary>The report this database holds, or null when no strip has run here. Readable after
  ///     the fact, which is the whole point of writing it.</summary>
  /// <param name="database">The app database to read.</param>
  /// <param name="ct">Cancellation for the read.</param>
  public static async Task<EmbeddedImageStripReport?> ReadReportAsync(AppDatabase database,
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
        ? JsonSerializer.Deserialize<EmbeddedImageStripReport>(raw)
        : null;
  }

  /// <summary>The one pass that names the work: every candidate row's id and the totals the report
  ///     carries.</summary>
  private static (int Rows, int Entries, long Chars, long Decoded, List<long> Rowids) Measure(
      SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the statement is a literal declared in this class, never input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = CandidateSql;
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
    using SqliteDataReader reader = command.ExecuteReader();
    List<long> rowids = [];
    int entries = 0;
    long chars = 0;
    long decoded = 0;
    while (reader.Read())
    {
      rowids.Add(reader.GetInt64(0));
      entries += reader.GetInt32(1);
      chars += reader.GetInt64(2);
      decoded += reader.GetInt64(3);
    }

    return (rowids.Count, entries, chars, decoded, rowids);
  }

  /// <summary>Rewrites each candidate row's meta_json so its embedded payloads become a record of
  ///     what was there. Returns how many rows actually changed.</summary>
  private static int StripRows(SqliteConnection connection, SqliteTransaction transaction,
      IReadOnlyList<long> rowids)
  {
    using SqliteCommand read = connection.CreateCommand();
    read.Transaction = transaction;
    read.CommandText = "SELECT meta_json FROM agent_messages WHERE rowid = $rowid;";
    _ = read.Parameters.Add("$rowid", SqliteType.Integer);
    using SqliteCommand write = connection.CreateCommand();
    write.Transaction = transaction;
    write.CommandText = "UPDATE agent_messages SET meta_json = $meta WHERE rowid = $rowid;";
    _ = write.Parameters.Add("$meta", SqliteType.Text);
    _ = write.Parameters.Add("$rowid", SqliteType.Integer);

    int stripped = 0;
    foreach (long rowid in rowids)
    {
      read.Parameters["$rowid"].Value = rowid;
      if (read.ExecuteScalar() is not string metaJson)
      {
        continue;
      }

      if (WithoutEmbeddedPayloads(metaJson) is not { } strippedJson)
      {
        continue;
      }

      write.Parameters["$meta"].Value = strippedJson;
      write.Parameters["$rowid"].Value = rowid;
      if (write.ExecuteNonQuery() > 0)
      {
        stripped++;
      }
    }

    return stripped;
  }

  /// <summary>The row's meta_json with every embedded payload replaced by the size of what was
  ///     removed, or null when the row held none. The media type stays - it is what makes the
  ///     marker say 'a screenshot was here' rather than 'something was here' - and every other
  ///     field of the document is carried through untouched.
  ///     <para>A PART THAT ALREADY NAMES A FILE KEEPS ITS PATH: removing a payload never removes a
  ///     reference, so a row written by a build that stored both cannot lose its picture.</para>
  ///     </summary>
  private static string? WithoutEmbeddedPayloads(string metaJson)
  {
    if (JsonNode.Parse(metaJson) is not JsonObject message
        || message["Parts"] is not JsonArray parts)
    {
      return null;
    }

    bool changed = false;
    foreach (JsonNode? node in parts)
    {
      if (node is not JsonObject part
          || part["Base64Data"] is not JsonValue value
          || !value.TryGetValue(out string? base64)
          || base64 is null)
      {
        continue;
      }

      part["RemovedBytes"] = JsonValue.Create(ImagePartMarker.DecodedByteCount(base64));
      _ = part.Remove("Base64Data");
      changed = true;
    }

    return changed ? message.ToJsonString() : null;
  }

  /// <summary>Compacts the file so the space the payloads occupied is returned to the disk rather
  ///     than held as free pages. Returns the failure's message, or null on success.
  ///     <para>IT RUNS OUTSIDE ANY TRANSACTION, which is the ordering this whole class exists to
  ///     get right: VACUUM cannot run inside one, and a strip whose rows are committed but whose
  ///     file still holds 900 MB of free pages has not done what it was asked.</para></summary>
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

  /// <summary>The record this database already holds, if any.</summary>
  private static EmbeddedImageStripReport? ReadReport(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT payload_json FROM migration_reports WHERE migration_version = $version;";
    _ = command.Parameters.AddWithValue("$version", ReportVersion);
    return command.ExecuteScalar() is string raw
        ? JsonSerializer.Deserialize<EmbeddedImageStripReport>(raw)
        : null;
  }

  /// <summary>Writes the record, inside the strip's transaction when there is one so the
  ///     measurement and the rewrite land together.</summary>
  private static void WriteReport(SqliteConnection connection, SqliteTransaction? transaction,
      EmbeddedImageStripReport report)
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

  /// <summary>Creates the table the record lives in. V43 runs this before the strip, so the record
  ///     has a home whatever shape the database arrived in.</summary>
  /// <param name="connection">The connection the migration runs on.</param>
  public static void CreateReportTable(SqliteConnection connection)
  {
    ArgumentNullException.ThrowIfNull(connection);
    Execute(connection, """
        CREATE TABLE IF NOT EXISTS migration_reports (
            migration_version INTEGER PRIMARY KEY,
            payload_json      TEXT NOT NULL,
            recorded_at       TEXT NOT NULL
        );
        """);
  }

  /// <summary>The database file's size as the database itself accounts for it: every page the file
  ///     holds, free or not. That is the number a reclaim moves.</summary>
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
