using System.Globalization;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.FileClaims;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>The shared claim table in the app database (issue #363). Every conversation
/// host in a workspace is its own process, so a claim table held in one process's memory
/// protects nothing: two sessions editing the same file never see each other. The app
/// database is the one store every host already shares, so the table lives there.
/// Expiry is computed on read and there is no sweeper, so a dead holder self-clears.</summary>
public sealed class SqliteFileClaimStore : IFileClaimStore
{
  private readonly string _connectionString;

  public SqliteFileClaimStore(AppDatabase database)
  {
    ArgumentNullException.ThrowIfNull(database);
    // Not pooled: a claim check is an edit-time operation, not a hot path, and a pooled
    // connection would keep a handle on the app database after the check is over.
    _connectionString = new SqliteConnectionStringBuilder
    {
      DataSource = database.DatabasePath,
      Pooling = false,
    }.ToString();
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(_connectionString);
    connection.Open();
    return connection;
  }

  public FileClaimResult Acquire(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    string scope = WorkspaceIdentity.Require(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    using SqliteConnection connection = Open();
    using SqliteTransaction transaction = connection.BeginTransaction();
    FileClaim? live = Read(connection, transaction, scope, path, now);
    if (live is not null && !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
    {
      transaction.Commit();
      return new FileClaimResult(false, live.SessionId, live.Expiry);
    }

    DateTimeOffset expiry = now + FileClaimPolicy.ClaimTtl;
    Write(connection, transaction, scope, path, sessionId, expiry);
    transaction.Commit();
    return new FileClaimResult(true, sessionId, expiry);
  }

  public FileClaimResult Renew(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    string scope = WorkspaceIdentity.Require(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    using SqliteConnection connection = Open();
    using SqliteTransaction transaction = connection.BeginTransaction();
    FileClaim? live = Read(connection, transaction, scope, path, now);
    if (live is null)
    {
      transaction.Commit();
      return new FileClaimResult(false, null, null);
    }

    if (!string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
    {
      transaction.Commit();
      return new FileClaimResult(false, live.SessionId, live.Expiry);
    }

    DateTimeOffset expiry = now + FileClaimPolicy.ClaimTtl;
    Write(connection, transaction, scope, path, sessionId, expiry);
    transaction.Commit();
    return new FileClaimResult(true, sessionId, expiry);
  }

  public bool Release(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    string scope = WorkspaceIdentity.Require(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    using SqliteConnection connection = Open();
    using SqliteTransaction transaction = connection.BeginTransaction();
    FileClaim? live = Read(connection, transaction, scope, path, now);
    if (live is null || !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
    {
      transaction.Commit();
      return false;
    }

    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "DELETE FROM file_claims WHERE workspace_id = $ws AND path = $path;";
    _ = command.Parameters.AddWithValue("$ws", scope);
    _ = command.Parameters.AddWithValue("$path", path);
    _ = command.ExecuteNonQuery();
    transaction.Commit();
    return true;
  }

  public FileClaim? Peek(string workspaceId, string path, DateTimeOffset now)
  {
    string scope = WorkspaceIdentity.Require(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    using SqliteConnection connection = Open();
    using SqliteTransaction transaction = connection.BeginTransaction();
    FileClaim? live = Read(connection, transaction, scope, path, now);
    transaction.Commit();
    return live;
  }

  /// <summary>The live claim, or null. An expired row is deleted on read, which is the
  /// whole of the expiry policy: no sweeper, no timer, no deadlock.</summary>
  private static FileClaim? Read(
      SqliteConnection connection, SqliteTransaction transaction, string workspaceId, string path, DateTimeOffset now)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "SELECT session_id, expiry FROM file_claims WHERE workspace_id = $ws AND path = $path;";
    _ = command.Parameters.AddWithValue("$ws", workspaceId);
    _ = command.Parameters.AddWithValue("$path", path);
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
    {
      return null;
    }

    string sessionId = reader.GetString(0);
    DateTimeOffset expiry = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    reader.Close();
    if (expiry <= now)
    {
      using SqliteCommand delete = connection.CreateCommand();
      delete.Transaction = transaction;
      delete.CommandText = "DELETE FROM file_claims WHERE workspace_id = $ws AND path = $path;";
      _ = delete.Parameters.AddWithValue("$ws", workspaceId);
      _ = delete.Parameters.AddWithValue("$path", path);
      _ = delete.ExecuteNonQuery();
      return null;
    }

    return new FileClaim(sessionId, expiry);
  }

  private static void Write(
      SqliteConnection connection, SqliteTransaction transaction,
      string workspaceId, string path, string sessionId, DateTimeOffset expiry)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        INSERT INTO file_claims (workspace_id, path, session_id, expiry)
        VALUES ($ws, $path, $session, $expiry)
        ON CONFLICT (workspace_id, path) DO UPDATE SET
            session_id = excluded.session_id,
            expiry     = excluded.expiry;
        """;
    _ = command.Parameters.AddWithValue("$ws", workspaceId);
    _ = command.Parameters.AddWithValue("$path", path);
    _ = command.Parameters.AddWithValue("$session", sessionId);
    _ = command.Parameters.AddWithValue("$expiry", expiry.ToString("O", CultureInfo.InvariantCulture));
    _ = command.ExecuteNonQuery();
  }
}
