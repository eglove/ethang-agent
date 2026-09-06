using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed persistence for completed !-command runs: one row per run,
///     bound to the workspace given at construction. Ids come from the global
///     autoincrement counter; every read filters on the bound workspace, so another
///     workspace's ids are invisible — a Get or Latest on one fails
///     CommandRunNotFound rather than leaking. Expected failures flow as DomainErrors
///     through Result; storage faults surface as StorageUnavailable, never an
///     exception.</summary>
public sealed class SqliteCommandRunStore(AppDatabase database, string workspaceId) : ICommandRunStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("Workspace id must be a non-empty string.", nameof(workspaceId))
      : workspaceId;

  public async Task<Result<CommandRun>> AddAsync(CommandRun run, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(run);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007

      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.CommandText = """
            INSERT INTO command_runs (workspace_id, command, exit_code, timed_out, output, ran_at)
            VALUES (@ws, @command, @exit, @timedOut, @output, @ranAt);
            """;
        Add(insert, "@ws", _workspaceId);
        Add(insert, "@command", run.Command);
        Add(insert, "@exit", run.ExitCode);
        Add(insert, "@timedOut", run.TimedOut ? 1 : 0);
        Add(insert, "@output", run.Output);
        Add(insert, "@ranAt", run.RanAt.ToString("O", CultureInfo.InvariantCulture));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      using SqliteCommand idCommand = connection.CreateCommand();
      idCommand.CommandText = "SELECT last_insert_rowid();";
      int id = Convert.ToInt32(await idCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
      return Result.Success(run with { Id = id });
    }
    catch (SqliteException ex)
    {
      return Result.Failure<CommandRun>(Unavailable(ex));
    }
  }

  public async Task<Result<CommandRun>> GetAsync(int id, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      return await ReadAsync(connection, id, ct).ConfigureAwait(false);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<CommandRun>(Unavailable(ex));
    }
  }

  public async Task<Result<CommandRun>> GetLatestAsync(CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
            SELECT id, command, exit_code, timed_out, output, ran_at
            FROM command_runs WHERE workspace_id = @ws ORDER BY id DESC LIMIT 1;
            """;
      Add(command, "@ws", _workspaceId);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      return !await reader.ReadAsync(ct).ConfigureAwait(false)
          ? Result.Failure<CommandRun>(new DomainError("CommandRunNotFound",
              "No command runs are recorded in this workspace yet."))
          : Result.Success(Map(reader));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<CommandRun>(Unavailable(ex));
    }
  }

  private async Task<Result<CommandRun>> ReadAsync(SqliteConnection connection, int id, CancellationToken ct)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT id, command, exit_code, timed_out, output, ran_at
            FROM command_runs WHERE workspace_id = @ws AND id = @id;
            """;
    Add(command, "@ws", _workspaceId);
    Add(command, "@id", id);
    using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
    return !await reader.ReadAsync(ct).ConfigureAwait(false)
        ? Result.Failure<CommandRun>(new DomainError("CommandRunNotFound",
            $"No command run with id {id} is recorded in this workspace."))
        : Result.Success(Map(reader));
  }

  private static CommandRun Map(SqliteDataReader reader)
  {
    return new CommandRun(
        reader.GetInt32(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetInt64(3) != 0,
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture));
  }

  private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);

  private static DomainError Unavailable(SqliteException ex) => new("StorageUnavailable", ex.Message);
}
