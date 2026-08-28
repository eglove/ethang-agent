using System.Globalization;
using eThangAgent.AgentDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The workspace-binding migration (V8) against the real SQLite store: a
///     database persisted by the pre-binding build (version 7, agents without the two
///     columns) must upgrade in place — legacy rows stay with NULL bindings — and the
///     new columns must round-trip through the store afterwards.</summary>
public sealed class SessionBindingMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-binding-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031
  }

  /// <summary>Builds a V7-shaped database: the original agents/agent_messages schema and
  ///     one legacy root row, stamped user_version = 7.</summary>
  private void CreateLegacyV7Database()
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    string sql = """
            CREATE TABLE agents (
                id             TEXT PRIMARY KEY,
                parent_id      TEXT NULL,
                depth          INTEGER NOT NULL,
                status         INTEGER NOT NULL,
                failure_reason INTEGER NULL,
                model_used     TEXT NOT NULL,
                label          TEXT NULL,
                task_prompt    TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                completed_at   TEXT NULL,
                final_report   TEXT NULL
            );
            CREATE TABLE agent_messages (
                agent_id  TEXT NOT NULL,
                seq       INTEGER NOT NULL,
                role      TEXT NOT NULL,
                content   TEXT NOT NULL,
                meta_json TEXT NULL,
                PRIMARY KEY (agent_id, seq)
            );
            INSERT INTO agents (id, parent_id, depth, status, failure_reason, model_used, label, task_prompt, created_at, completed_at, final_report)
            VALUES ('legacy-root', NULL, 0, 2, NULL, 'unassigned', 'root', 'conversation root', '2026-01-01T00:00:00.0000000+00:00', NULL, NULL);
            """;
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = $"PRAGMA user_version = 7;";
    _ = version.ExecuteNonQuery();
  }

  [Fact]
  public void LegacyV7Database_Upgrades_InPlace_LegacyRowKeepsNullBinding()
  {
    CreateLegacyV7Database();

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(8, Version(connection));
    Assert.Equal(2L, Scalar(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name IN ('workspace_id', 'provider');"));
    // The legacy row survives with NULL bindings — the catalog skips it, resume
    // reports it NotResumable.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM agents WHERE id = 'legacy-root' AND workspace_id IS NULL AND provider IS NULL;"));
  }

  [Fact]
  public async Task AfterUpgrade_NewRootRoundTrips_WithBinding()
  {
    CreateLegacyV7Database();
    SqliteAgentStore store = new(new AppDatabase(_dbPath));

    AgentId rootId = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, @"C:\workspaces\demo", "openrouter"));

    AgentRecord record = (await store.GetAsync(rootId)).Value!;
    Assert.Equal(@"C:\workspaces\demo", record.WorkspaceId);
    Assert.Equal("openrouter", record.Provider);
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    return connection;
  }

  private static int Version(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static long Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // constant test-only SQL
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
