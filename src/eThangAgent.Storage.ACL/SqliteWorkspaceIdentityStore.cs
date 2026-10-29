using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite-backed workspace identity bindings (issue #317 D5): the one table that maps a
///     stable workspace id to the directory path it currently lives at.
///     <para>This store is deliberately UNSCOPED - it is the table that DEFINES the scopes, so
///     binding it to one workspace at construction (the pattern every other store follows) would
///     make it unable to answer the question it exists for. It is the only Storage ACL store with
///     no workspace constructor argument, and that is a named decision rather than an oversight.
///     </para>
///     <para>Expected failures flow as <see cref="DomainError"/>s through <see cref="Result{T}"/>;
///     storage faults surface as a StorageUnavailable failure, never an exception.</para></summary>
public sealed class SqliteWorkspaceIdentityStore(AppDatabase database) : IWorkspaceIdentityStore
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<WorkspaceIdentityBinding?>> FindByIdAsync(string id, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    return await FindAsync("SELECT id, root FROM workspace_identities WHERE id=@key;", id, ct).ConfigureAwait(false);
  }

  public async Task<Result<WorkspaceIdentityBinding?>> FindByRootAsync(string root, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    // Roots are compared in their NORMALIZED form on both sides: the resolver stores what the
    // caller opened, and a trailing separator is not a different directory.
    return await FindAsync("SELECT id, root FROM workspace_identities WHERE root=@key;",
        WorkspaceIdentity.Normalize(root), ct).ConfigureAwait(false);
  }

  public async Task<Result<WorkspaceIdentityBinding>> BindAsync(string id, string root,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    string normalized = WorkspaceIdentity.Normalize(root);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      // Both sides are cleared first. A MOVE re-points one id at a new path, and a COPY mints a
      // fresh id for a path the original still claims - either way a stale row on the other side
      // would leave two identities fighting over one directory, which is exactly what D6 forbids.
      using (SqliteCommand clear = connection.CreateCommand())
      {
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM workspace_identities WHERE id=@id OR root=@root;";
        _ = clear.Parameters.AddWithValue("@id", id.Trim());
        _ = clear.Parameters.AddWithValue("@root", normalized);
        _ = await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO workspace_identities (id, root, bound_at) VALUES (@id, @root, @at);";
        _ = insert.Parameters.AddWithValue("@id", id.Trim());
        _ = insert.Parameters.AddWithValue("@root", normalized);
        _ = insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(new WorkspaceIdentityBinding(id.Trim(), normalized));
    }
    catch (SqliteException ex)
    {
      return Result.Failure<WorkspaceIdentityBinding>(Unavailable(ex));
    }
  }

  public async Task<Result<IReadOnlyList<WorkspaceIdentityBinding>>> ListAsync(CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "SELECT id, root FROM workspace_identities ORDER BY bound_at DESC, id;";
      List<WorkspaceIdentityBinding> bindings = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        bindings.Add(new WorkspaceIdentityBinding(reader.GetString(0), reader.GetString(1)));
      }

      return Result.Success<IReadOnlyList<WorkspaceIdentityBinding>>(bindings);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<IReadOnlyList<WorkspaceIdentityBinding>>(Unavailable(ex));
    }
  }

  private async Task<Result<WorkspaceIdentityBinding?>> FindAsync(string sql, string key, CancellationToken ct)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      // Named decision (CA2100): every caller passes a call-site-constant statement; the only
      // variable part is the bound @key parameter.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
      command.CommandText = sql;
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
      _ = command.Parameters.AddWithValue("@key", key);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      return await reader.ReadAsync(ct).ConfigureAwait(false)
          ? Result.Success<WorkspaceIdentityBinding?>(new WorkspaceIdentityBinding(reader.GetString(0), reader.GetString(1)))
          : Result.Success<WorkspaceIdentityBinding?>(null);
    }
    catch (SqliteException ex)
    {
      return Result.Failure<WorkspaceIdentityBinding?>(Unavailable(ex));
    }
  }

  private static DomainError Unavailable(SqliteException ex) => new("StorageUnavailable",
      $"the workspace identity table could not be read or written: {ex.Message}");
}
