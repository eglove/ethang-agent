using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Single app-owned SQLite database. One database serves many workspaces —
///     rows are keyed by workspace id. Schema changes go through versioned migrations.
///     This database is the beachhead for later app tables (kanban, agent statuses).</summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string? databasePath = null)
    {
        var path = databasePath
            ?? Environment.GetEnvironmentVariable("ETHANG_AGENT_DB")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "eThangAgent", "eThangAgent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        if (GetVersion(connection) >= 1) return;
        ApplyV1(connection);
        SetVersion(connection, 1);
    }

    private static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void ApplyV1(SqliteConnection connection)
    {
        var sql = """
            CREATE TABLE IF NOT EXISTS state_keys (
                workspace_id TEXT NOT NULL,
                ns           TEXT NOT NULL,
                name         TEXT NOT NULL,
                value        TEXT NOT NULL,
                version      INTEGER NOT NULL,
                updated_at   TEXT NOT NULL,
                PRIMARY KEY (workspace_id, ns, name)
            );
            CREATE TABLE IF NOT EXISTS transitions (
                id            TEXT PRIMARY KEY,
                workspace_id  TEXT NOT NULL,
                from_state    TEXT NOT NULL,
                to_state      TEXT NOT NULL,
                summary       TEXT NOT NULL,
                evidence_json TEXT NOT NULL,
                status        TEXT NOT NULL,
                created_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transitions_ws_status ON transitions (workspace_id, status);
            CREATE TABLE IF NOT EXISTS state_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id TEXT NOT NULL,
                kind         TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                occurred_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_state_events_ws ON state_events (workspace_id, id);
            """;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
