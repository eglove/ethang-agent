using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The app-level retention sweep (issue #246): rows older than a configured window,
///     from a named set of tables, followed by a reclaim that returns the space to the disk.
///     <para>THE TABLE LIST IS EXACT AND NAMED. <c>agent_messages</c> goes through its
///     <c>agents</c> row; <c>agents</c>, <c>tool_output_archive</c>, <c>command_runs</c>,
///     <c>state_keys</c> and <c>watchdog_events</c> are swept independently. Nothing else is
///     touched, and no table joins the list without a measurement of what it holds.</para>
///     <para><c>chains</c> AND <c>chain_steps</c> ARE EXCLUDED BY TABLE, NOT RETAINED LONGER: a
///     chain is the record of which sessions ran, what each step concluded and what it cost, and
///     when a workspace directory is gone it is the only surviving evidence of that work. A chain
///     is deleted only by an explicit user action on a specific chain, never by a timer.</para>
///     <para>THE RECLAIM IS NOT OPTIONAL. A plain DELETE leaves free pages and the file does not
///     move: the same database measured 2,364.1 MiB before a strip and after it, with the
///     VACUUM that followed taking it to 1,466.7 MiB. So the check is <c>freelist_count</c>
///     reaching zero, not merely that rows were deleted.</para></summary>
public sealed class RetentionSweep(AppDatabase database, IAppPreferenceStore preferences,
    TimeProvider clock) : IRetentionService
{
  /// <summary>The preference key the window is configured under, in hours. Absent means the
  ///     default; zero means retention is disabled.</summary>
  public const string WindowHoursKey = "retention.window_hours";

  /// <summary>The default window: thirty days.</summary>
  public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly IAppPreferenceStore _preferences =
      preferences ?? throw new ArgumentNullException(nameof(preferences));
  private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

  /// <summary>Reads the configured window, or null when retention is disabled.
  ///     <para>A RUN WITH NO WINDOW DELETES NOTHING AND RECLAIMS NOTHING: disabling retention is
  ///     expressed by the welcome tab's own setting, and a run that ignored it would delete data
  ///     the user asked to keep.</para></summary>
  /// <param name="ct">Cancellation for the read.</param>
  public async Task<TimeSpan?> ConfiguredWindowAsync(CancellationToken ct = default)
  {
    string? raw = await _preferences.GetAsync(WindowHoursKey, ct).ConfigureAwait(false);
    if (raw is null)
    {
      return DefaultWindow;
    }

    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double hours)
        || hours < 0)
    {
      // A stored value nobody can read is not a licence to delete: the default applies, and the
      // setting surface is where a wrong value is corrected.
      return DefaultWindow;
    }

    return hours == 0 ? null : TimeSpan.FromHours(hours);
  }

  /// <summary>Deletes rows older than the window and reclaims the file.
  ///     <para>THE CASCADE ORDER IS STATED RATHER THAN LEFT TO THE ENGINE: a transcript is
  ///     deleted with its agent, so no <c>agent_messages</c> row can outlive the <c>agents</c> row
  ///     it belongs to. <c>agent_messages</c> has no foreign key to enforce that, so the order
  ///     here is the only thing that does.</para></summary>
  /// <param name="window">How old a row must be to go.</param>
  /// <param name="ct">Cancellation for the run.</param>
  public async Task<Result<RetentionRun>> RunAsync(TimeSpan window, CancellationToken ct = default)
  {
    if (window <= TimeSpan.Zero)
    {
      return Result.Failure<RetentionRun>(new DomainError("InvalidRetentionWindow",
          "a retention window must be longer than zero; disable retention rather than setting one."));
    }

    DateTimeOffset cutoff = _clock.GetUtcNow().Subtract(window);
    string stamp = cutoff.ToString("o", CultureInfo.InvariantCulture);
    long bytesBefore = await ScalarAsync("PRAGMA page_count;", ct).ConfigureAwait(false)
        * await ScalarAsync("PRAGMA page_size;", ct).ConfigureAwait(false);
    Dictionary<string, int> deleted = [];

    // ONE TRANSACTION: a sweep that deleted transcripts and then could not delete their agents
    // would leave rows pointing at records nobody can find. All of it lands or none of it does.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    // Named decision (CA1849, S6966): a SQLite transaction is a statement on the connection and
    // there is no asynchronous form to await; the call is the same one every other store in this
    // assembly makes, and it does not wait on I/O.
#pragma warning disable CA1849, S6966 // Call async methods when in an async method
    // THE TABLE LIST IS EXACT AND NAMED (issue #246). A table a database from an older build
    // does not have is SKIPPED rather than fatal: a migration runs against every historical
    // shape, and a sweep that failed on a table it was told to touch would stop retaining
    // anything at all on such a database.
    (string Table, string Sql)[] sweeps =
    [
      ("agents", "DELETE FROM agents WHERE created_at < $cutoff;"),
      ("tool_output_archive", "DELETE FROM tool_output_archive WHERE created_at < $cutoff;"),
      ("command_runs", "DELETE FROM command_runs WHERE ran_at < $cutoff;"),
      ("state_keys", "DELETE FROM state_keys WHERE updated_at < $cutoff;"),
      ("watchdog_events", "DELETE FROM watchdog_events WHERE COALESCE(last_seen_at, created_at) < $cutoff;"),
    ];

    using (SqliteTransaction transaction = connection.BeginTransaction())
#pragma warning restore CA1849, S6966
    {
      deleted["agent_messages"] = await DeleteAgentMessagesAsync(connection, transaction, stamp, ct)
          .ConfigureAwait(false);
      foreach ((string table, string sql) in sweeps)
      {
        deleted[table] = HasTable(connection, transaction, table)
            ? await ExecuteAsync(connection, transaction, sql, stamp, ct).ConfigureAwait(false)
            : 0;
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    string? reclaimFailure = await ReclaimAsync(ct).ConfigureAwait(false);
    long bytesAfter = await ScalarAsync("PRAGMA page_count;", ct).ConfigureAwait(false)
        * await ScalarAsync("PRAGMA page_size;", ct).ConfigureAwait(false);
    long freePages = await ScalarAsync("PRAGMA freelist_count;", ct).ConfigureAwait(false);
    return Result.Success(new RetentionRun(window, cutoff, deleted, bytesBefore, bytesAfter,
        freePages, reclaimFailure, _clock.GetUtcNow()));
  }

  /// <summary>The port the app-level loop drives: the configured window, or null when retention
  ///     is disabled, and then the sweep itself.
  ///     <para>A DISABLED WINDOW IS NOT A FAILURE: it is the user's own instruction, and the
  ///     caller reports it as 'nothing to do' rather than as an error worth surfacing.</para>
  ///     </summary>
  /// <param name="ct">Cancellation for the run.</param>
  public async Task<Result<RetentionRun?>> RunOnceAsync(CancellationToken ct = default)
  {
    TimeSpan? window = await ConfiguredWindowAsync(ct).ConfigureAwait(false);
    if (window is null)
    {
      return Result.Success<RetentionRun?>(null);
    }

    Result<RetentionRun> run = await RunAsync(window.Value, ct).ConfigureAwait(false);
    return run.IsSuccess
        ? Result.Success<RetentionRun?>(run.Value)
        : Result.Failure<RetentionRun?>(run.Error);
  }

  /// <summary>Whether a table exists on this connection's database, read through the transaction
  ///     the sweep is running in so the answer belongs to the same snapshot as its deletes.
  ///     </summary>
  private static bool HasTable(SqliteConnection connection, SqliteTransaction transaction, string table)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
    _ = command.Parameters.AddWithValue("$name", table);
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
  }

  /// <summary>Deletes the transcripts of agents created before the cutoff, BEFORE those agents
  ///     are deleted, so no message row is left pointing at an agent that is gone.</summary>
  private static async Task<int> DeleteAgentMessagesAsync(SqliteConnection connection,
      SqliteTransaction transaction, string cutoff, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        DELETE FROM agent_messages
         WHERE agent_id IN (SELECT id FROM agents WHERE created_at < $cutoff);
        """;
    _ = command.Parameters.AddWithValue("$cutoff", cutoff);
    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction,
      string sql, string cutoff, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    // Named decision (CA2100): every caller passes a literal declared in this class.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.Parameters.AddWithValue("$cutoff", cutoff);
    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
  }

  /// <summary>Compacts the file so the deleted rows' space returns to the disk. Returns the
  ///     failure's message, or null on success.
  ///     <para>IT RUNS OUTSIDE THE DELETE'S TRANSACTION, because VACUUM cannot run inside one.
  ///     </para></summary>
  private async Task<string?> ReclaimAsync(CancellationToken ct)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "PRAGMA busy_timeout = 5000;";
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      using SqliteCommand vacuum = connection.CreateCommand();
      vacuum.CommandText = "VACUUM;";
      _ = await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return null;
    }
    catch (SqliteException ex)
    {
      return ex.Message;
    }
  }

  private async Task<long> ScalarAsync(string sql, CancellationToken ct)
  {
    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal declared in this class.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
        CultureInfo.InvariantCulture);
  }
}
