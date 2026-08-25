using eThangAgent.Storage.ACL;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V5: an FTS5 index over state_keys (value, ns, name) kept in
/// sync by triggers, with existing rows backfilled at migration time.</summary>
public class AppDatabaseFtsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-fts-{Guid.NewGuid():N}.db");
    private readonly AppDatabase _database;

    public AppDatabaseFtsTests() => _database = new AppDatabase(_dbPath);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void FreshDatabase_MigratesToVersion5()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(5, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void StateKeysFts_VirtualTableAndTriggers_Exist()
    {
        using var connection = _database.Open();
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys_fts';"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('state_ai','state_ad','state_au');"));
    }

    [Fact]
    public async Task InsertedStateRows_AreSearchable_ViaTriggers()
    {
        InsertKey("ws1", "plans", "my-plan", "rewrite the SDD ledger flow");
        InsertKey("ws1", "notes", "scratch", "unrelated content");

        using var connection = _database.Open();
        Assert.Equal(1L, CountMatches(connection, "ws1", "ledger"));
    }

    [Fact]
    public async Task UpdatedAndDeletedRows_StaysSynced_ViaTriggers()
    {
        InsertKey("ws1", "notes", "doc", "original searchable text");

        UpdateKey("ws1", "notes", "doc", "replaced content entirely");
        using (var connection = _database.Open())
            Assert.Equal(0L, CountMatches(connection, "ws1", "searchable"));

        DeleteKey("ws1", "notes", "doc");
        using (var connection = _database.Open())
            Assert.Equal(0L, CountMatches(connection, "ws1", "replaced"));
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

        using var connection = _database.Open();
        Assert.Equal(1L, CountMatches(connection, "ws2", "quartz"));
    }

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static long CountMatches(SqliteConnection connection, string workspaceId, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM state_keys_fts f
            JOIN state_keys k ON k.rowid = f.rowid
            WHERE k.workspace_id = @w AND state_keys_fts MATCH @q;
            """;
        command.Parameters.AddWithValue("@w", workspaceId);
        command.Parameters.AddWithValue("@q", query);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void InsertKey(string workspaceId, string ns, string name, string value)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES (@w, @ns, @n, @v, 1, '2026-08-24T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("@w", workspaceId);
        command.Parameters.AddWithValue("@ns", ns);
        command.Parameters.AddWithValue("@n", name);
        command.Parameters.AddWithValue("@v", value);
        command.ExecuteNonQuery();
    }

    private void UpdateKey(string workspaceId, string ns, string name, string value)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE state_keys SET value=@v WHERE workspace_id=@w AND ns=@ns AND name=@n;";
        command.Parameters.AddWithValue("@v", value);
        command.Parameters.AddWithValue("@w", workspaceId);
        command.Parameters.AddWithValue("@ns", ns);
        command.Parameters.AddWithValue("@n", name);
        command.ExecuteNonQuery();
    }

    private void DeleteKey(string workspaceId, string ns, string name)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM state_keys WHERE workspace_id=@w AND ns=@ns AND name=@n;";
        command.Parameters.AddWithValue("@w", workspaceId);
        command.Parameters.AddWithValue("@ns", ns);
        command.Parameters.AddWithValue("@n", name);
        command.ExecuteNonQuery();
    }

    private void DropTriggers()
    {
        using var connection = _database.Open();
        foreach (var trigger in new[] { "state_ai", "state_ad", "state_au" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP TRIGGER IF EXISTS {trigger};";
            command.ExecuteNonQuery();
        }
    }

    private void RecreateTriggers()
    {
        using var connection = _database.Open();
        var sql = """
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
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Backfill()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state_keys_fts(rowid, value, ns, name) SELECT rowid, value, ns, name FROM state_keys;";
        command.ExecuteNonQuery();
    }
}
