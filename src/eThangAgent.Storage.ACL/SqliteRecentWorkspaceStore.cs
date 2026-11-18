using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed recent-workspace list over the shared app database.
///     <para>UNSCOPED BY DESIGN: the list exists to answer 'which directories has this app
///     opened', so binding it to one workspace at construction would defeat it. It is the same
///     shape <see cref="SqliteWorkspaceIdentityStore"/> takes, and for the same reason.</para>
///     <para>Every root is normalized on the way in and on the way out, so a trailing separator
///     or a case difference is one directory rather than two.</para></summary>
public sealed class SqliteRecentWorkspaceStore(AppDatabase database) : IRecentWorkspaceStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<IReadOnlyList<RecentWorkspace>>> ListAsync(int limit,
      CancellationToken ct = default)
  {
    if (limit < 1)
    {
      return Result.Failure<IReadOnlyList<RecentWorkspace>>(new DomainError("InvalidLimit",
          $"the recent-workspace limit must be at least 1; {limit.ToString(CultureInfo.InvariantCulture)} was given."));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          "SELECT root, opened_at FROM recent_workspaces ORDER BY opened_at DESC, root LIMIT $limit;";
      _ = command.Parameters.AddWithValue("$limit", limit);
      List<RecentWorkspace> rows = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        rows.Add(new RecentWorkspace(reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind)));
      }

      return Result.Success<IReadOnlyList<RecentWorkspace>>(rows);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<RecentWorkspace>>(Unavailable(ex));
    }
  }

  public async Task<Result<bool>> RecordAsync(string root, CancellationToken ct = default)
  {
    string normalized = WorkspaceIdentity.Normalize(root);
    if (normalized.Length == 0)
    {
      return Result.Failure<bool>(new DomainError("InvalidWorkspaceRoot",
          "a workspace root is required to record a recent workspace."));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText =
          """
          INSERT INTO recent_workspaces (root, opened_at)
          VALUES ($root, $opened)
          ON CONFLICT(root) DO UPDATE SET opened_at = $opened;
          """;
      _ = command.Parameters.AddWithValue("$root", normalized);
      _ = command.Parameters.AddWithValue("$opened",
          DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<bool>(Unavailable(ex));
    }
  }

  public async Task<Result<bool>> ForgetAsync(string root, CancellationToken ct = default)
  {
    string normalized = WorkspaceIdentity.Normalize(root);
    if (normalized.Length == 0)
    {
      return Result.Failure<bool>(new DomainError("InvalidWorkspaceRoot",
          "a workspace root is required to forget a recent workspace."));
    }

    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM recent_workspaces WHERE root = $root;";
      _ = command.Parameters.AddWithValue("$root", normalized);
      _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<bool>(Unavailable(ex));
    }
  }

  private static DomainError Unavailable(SqliteException ex) => new("StorageUnavailable",
      $"the recent-workspace table could not be read or written: {ex.Message}");
}
