using System.Globalization;
using eThangAgent.AgentDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V18 (issue #236): the curated-memories feature was removed, so the
///     migration drops its table, its FTS5 index, its sync triggers, and its index. A V17
///     database holding curated rows upgrades in place, loses exactly that state, and keeps
///     every other table and row; re-opening is a no-op.</summary>
public sealed class CuratedMemoryRemovalMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-v18mig-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  /// <summary>Builds a fully-migrated database carrying real rows, recreates the V4
  ///     curated-memories shape V18 removes, seeds one row into it, and stamps
  ///     user_version 17 - the exact pre-V18 state.</summary>
  private async Task SeedV17ShapeAsync()
  {
    AppDatabase db = new(_dbPath);
    AgentRecord root = AgentRecord.Root(AgentId.NewId(), DateTimeOffset.UtcNow, @"C:\workspaces\demo", "openrouter");
    _ = await new SqliteAgentStore(db).SaveAsync(root).ConfigureAwait(false);

    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the SQL is a literal fixture, not user input.
#pragma warning disable CA2100
    command.CommandText = string.Join('\n',
        "CREATE TABLE curated_memories (",
        "    id            TEXT PRIMARY KEY,",
        "    workspace_id  TEXT NOT NULL,",
        "    category      TEXT NOT NULL,",
        "    tags          TEXT NOT NULL,",
        "    content       TEXT NOT NULL,",
        "    usage_hint    TEXT NULL,",
        "    scope         TEXT NOT NULL,",
        "    provenance    TEXT NULL,",
        "    version       INTEGER NOT NULL,",
        "    created_at    TEXT NOT NULL,",
        "    updated_at    TEXT NOT NULL",
        ");",
        "CREATE INDEX ix_curated_ws ON curated_memories (workspace_id, scope);",
        "CREATE VIRTUAL TABLE curated_memories_fts USING fts5(",
        "    content, tags, usage_hint, content='curated_memories', content_rowid='rowid'",
        ");",
        "CREATE TRIGGER curated_ai AFTER INSERT ON curated_memories BEGIN",
        "    INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)",
        "    VALUES (new.rowid, new.content, new.tags, new.usage_hint);",
        "END;",
        "CREATE TRIGGER curated_ad AFTER DELETE ON curated_memories BEGIN",
        "    INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)",
        "    VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);",
        "END;",
        "CREATE TRIGGER curated_au AFTER UPDATE ON curated_memories BEGIN",
        "    INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)",
        "    VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);",
        "    INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)",
        "    VALUES (new.rowid, new.content, new.tags, new.usage_hint);",
        "END;",
        "INSERT INTO curated_memories",
        "    (id, workspace_id, category, tags, content, usage_hint, scope, provenance, version, created_at, updated_at)",
        "VALUES",
        "    ('row-1', 'ws-demo', 'Convention', '[]', 'a stale curated note', NULL, 'Workspace', NULL, 1, 't', 't');",
        "PRAGMA user_version = 17;");
#pragma warning restore CA2100
    _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
  }

  [Fact]
  public async Task V18_DropsCuratedObjects_And_Leaves_Other_Rows_Untouched()
  {
    await SeedV17ShapeAsync().ConfigureAwait(true);

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(22, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'curated%';"));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_curated_ws';"));
    // Every earlier table and row is untouched.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM agents;"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys';"));
  }

  [Fact]
  public async Task V18_Is_Idempotent_Reopening_Is_A_NoOp()
  {
    await SeedV17ShapeAsync().ConfigureAwait(true);

    _ = new AppDatabase(_dbPath); // migrate once
    _ = new AppDatabase(_dbPath); // reopen: no-op

    using SqliteConnection connection = Open();
    Assert.Equal(22, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'curated%';"));
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
    // Named decision (CA2100): callers pass literal fixture SQL, never user input.
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
