using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V5: an FTS5 index over state_keys (value, ns, name) kept in
/// sync by triggers, with existing rows backfilled at migration time.</summary>
public sealed class AppDatabaseFtsTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-fts-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;

  public AppDatabaseFtsTests() => _database = new AppDatabase(_dbPath);

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public void FreshDatabase_MigratesToLatestVersion()
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    Assert.Equal(10, Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
  }

  [Fact]
  public void StateKeysFts_VirtualTableAndTriggers_Exist()
  {
    using SqliteConnection connection = _database.Open();
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys_fts';"));
    Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('state_ai','state_ad','state_au');"));
  }

  [Fact]
  public Task InsertedStateRows_AreSearchable_ViaTriggers()
  {
    InsertKey("ws1", "plans", "my-plan", "rewrite the SDD ledger flow");
    InsertKey("ws1", "notes", "scratch", "unrelated content");

    using SqliteConnection connection = _database.Open();
    Assert.Equal(1L, CountMatches(connection, "ws1", "ledger"));
    return Task.CompletedTask;
  }

  [Fact]
  public Task UpdatedAndDeletedRows_StaysSynced_ViaTriggers()
  {
    InsertKey("ws1", "notes", "doc", "original searchable text");

    UpdateKey("ws1", "notes", "doc", "replaced content entirely");
    using (SqliteConnection connection = _database.Open())
    {
      Assert.Equal(0L, CountMatches(connection, "ws1", "searchable"));
    }

    DeleteKey("ws1", "notes", "doc");
    using (SqliteConnection connection = _database.Open())
    {
      Assert.Equal(0L, CountMatches(connection, "ws1", "replaced"));
    }

    return Task.CompletedTask;
  }

  [Fact]
  public void Backfill_CoversPreExistingRows()
  {
    // Simulate a pre-V5 row: insert while the sync triggers are dropped,
    // then re-run the migration's backfill statement.
    DropTriggers();
    InsertKey("ws2", "notes", "legacy", "quartz tuning fork");
    RecreateTriggers();
    Backfill();

    using SqliteConnection connection = _database.Open();
    Assert.Equal(1L, CountMatches(connection, "ws2", "quartz"));
  }

  private long Scalar(string sql)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): test helper runs test-authored constant SQL only.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static long CountMatches(SqliteConnection connection, string workspaceId, string query)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT COUNT(*) FROM state_keys_fts f
            JOIN state_keys k ON k.rowid = f.rowid
            WHERE k.workspace_id = @w AND state_keys_fts MATCH @q;
            """;
    _ = command.Parameters.AddWithValue("@w", workspaceId);
    _ = command.Parameters.AddWithValue("@q", query);
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private void InsertKey(string workspaceId, string ns, string name, string value)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES (@w, @ns, @n, @v, 1, '2026-08-24T00:00:00.0000000+00:00');
            """;
    _ = command.Parameters.AddWithValue("@w", workspaceId);
    _ = command.Parameters.AddWithValue("@ns", ns);
    _ = command.Parameters.AddWithValue("@n", name);
    _ = command.Parameters.AddWithValue("@v", value);
    _ = command.ExecuteNonQuery();
  }

  private void UpdateKey(string workspaceId, string ns, string name, string value)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE state_keys SET value=@v WHERE workspace_id=@w AND ns=@ns AND name=@n;";
    _ = command.Parameters.AddWithValue("@v", value);
    _ = command.Parameters.AddWithValue("@w", workspaceId);
    _ = command.Parameters.AddWithValue("@ns", ns);
    _ = command.Parameters.AddWithValue("@n", name);
    _ = command.ExecuteNonQuery();
  }

  private void DeleteKey(string workspaceId, string ns, string name)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "DELETE FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
    _ = command.Parameters.AddWithValue("@w", workspaceId);
    _ = command.Parameters.AddWithValue("@ns", ns);
    _ = command.Parameters.AddWithValue("@n", name);
    _ = command.ExecuteNonQuery();
  }

  private void DropTriggers()
  {
    using SqliteConnection connection = _database.Open();
    foreach (string? trigger in new[] { "state_ai", "state_ad", "state_au" })
    {
      using SqliteCommand command = connection.CreateCommand();
      // Named decision (CA2100): trigger names come from a fixed test list.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
      command.CommandText = $"DROP TRIGGER IF EXISTS {trigger};";
#pragma warning restore CA2100
      _ = command.ExecuteNonQuery();
    }
  }

  private void RecreateTriggers()
  {
    using SqliteConnection connection = _database.Open();
    string sql = """
            CREATE TRIGGER IF NOT EXISTS state_ai AFTER INSERT ON state_keys BEGIN
                INSERT INTO state_keys_fts(rowid, value, ns, name)
                VALUES (new.rowid, new.value, new.ns, new.name);
            END;
            CREATE TRIGGER IF NOT EXISTS state_ad AFTER DELETE ON state_keys BEGIN
                INSERT INTO state_keys_fts(state_keys_fts, rowid, value, ns, name)
                VALUES ('delete', old.rowid, old.value, old.ns, old.name);
            END;
            CREATE TRIGGER IF NOT EXISTS state_au AFTER UPDATE ON state_keys BEGIN
                INSERT INTO state_keys_fts(state_keys_fts, rowid, value, ns, name)
                VALUES ('delete', old.rowid, old.value, old.ns, old.name);
                INSERT INTO state_keys_fts(rowid, value, ns, name)
                VALUES (new.rowid, new.value, new.ns, new.name);
            END;
            """;
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
  }

  private void Backfill()
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "INSERT INTO state_keys_fts(rowid, value, ns, name) SELECT rowid, value, ns, name FROM state_keys;";
    _ = command.ExecuteNonQuery();
  }
}
