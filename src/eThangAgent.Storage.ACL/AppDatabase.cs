using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Single app-owned SQLite database. One database serves many workspaces —
///     rows are keyed by workspace id. Schema changes go through versioned migrations.
///     This database is the beachhead for later app tables (kanban, agent statuses).</summary>
public sealed class AppDatabase
{
  private readonly string _connectionString;
  private readonly string _readOnlyConnectionString;

  /// <summary>Process-wide migration gate. Containers may be constructed concurrently
  ///     (one per session), and two Migrate() passes over the same file must not
  ///     interleave: user_version is read before the schema change, so a concurrent
  ///     pass can re-apply a migration the other just committed. SQLite serializes
  ///     writers at the file level, but not the check-then-apply pair.</summary>
  private static readonly SemaphoreSlim MigrationGate = new(1, 1);

  public AppDatabase(string? databasePath = null)
  {
    string path = databasePath
        ?? Environment.GetEnvironmentVariable("ETHANG_AGENT_DB")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eThangAgent", "eThangAgent.db");
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    SqliteConnectionStringBuilder builder = new() { DataSource = path };
    _connectionString = builder.ToString();
    builder.Mode = SqliteOpenMode.ReadOnly;
    _readOnlyConnectionString = builder.ToString();
    MigrationGate.Wait();
    try
    {
      Migrate();
    }
    finally
    {
      _ = MigrationGate.Release();
    }
  }

  public SqliteConnection Open()
  {
    SqliteConnection connection = new(_connectionString);
    connection.Open();
    return connection;
  }

  /// <summary>Read-only connection for self-inspection (the db_schema / db_query
  ///     tools). SQLite rejects every write at the engine level, so even a statement
  ///     that slips past the domain's lexical gate cannot mutate the database. The
  ///     file always exists: the constructor migrates before any caller runs.</summary>
  public SqliteConnection OpenReadOnly()
  {
    SqliteConnection connection = new(_readOnlyConnectionString);
    connection.Open();
    return connection;
  }

  private void Migrate()
  {
    using SqliteConnection connection = Open();
    if (GetVersion(connection) < 1)
    {
      ApplyV1(connection);
      SetVersion(connection, 1);
    }
    if (GetVersion(connection) < 2)
    {
      ApplyV2(connection);
      SetVersion(connection, 2);
    }
    if (GetVersion(connection) < 3)
    {
      ApplyV3(connection);
      SetVersion(connection, 3);
    }
    if (GetVersion(connection) < 4)
    {
      ApplyV4(connection);
      SetVersion(connection, 4);
    }
    if (GetVersion(connection) < 5)
    {
      ApplyV5(connection);
      SetVersion(connection, 5);
    }
    if (GetVersion(connection) < 6)
    {
      ApplyV6(connection);
      SetVersion(connection, 6);
    }
    if (GetVersion(connection) < 7)
    {
      ApplyV7(connection);
      SetVersion(connection, 7);
    }
    if (GetVersion(connection) < 8)
    {
      ApplyV8(connection);
      SetVersion(connection, 8);
    }
    if (GetVersion(connection) < 9)
    {
      ApplyV9(connection);
      SetVersion(connection, 9);
    }
    if (GetVersion(connection) < 10)
    {
      ApplyV10(connection);
      SetVersion(connection, 10);
    }
    if (GetVersion(connection) < 11)
    {
      ApplyV11(connection);
      SetVersion(connection, 11);
    }
  }

  private static int GetVersion(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static void SetVersion(SqliteConnection connection, int version)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100, S2077): value is a constant integer from our own migration
    // table, never user input. PRAGMA does not accept parameters, hence the interpolation.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
#pragma warning disable S2077 // Use a parameterized query instead of string formatting
    command.CommandText = $"PRAGMA user_version = {version};";
#pragma warning restore S2077 // Use a parameterized query instead of string formatting
#pragma warning restore CA2100 // Review SQL query for security vulnerabilities
    _ = command.ExecuteNonQuery();
  }

  private static void ApplyV1(SqliteConnection connection)
  {
    string sql = """
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
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }

  private static void ApplyV2(SqliteConnection connection)
  {
    string sql = """
            CREATE TABLE IF NOT EXISTS agents (
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
            CREATE TABLE IF NOT EXISTS agent_messages (
                agent_id  TEXT NOT NULL,
                seq       INTEGER NOT NULL,
                role      TEXT NOT NULL,
                content   TEXT NOT NULL,
                meta_json TEXT NULL,
                PRIMARY KEY (agent_id, seq)
            );
            CREATE TABLE IF NOT EXISTS agent_events (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id     TEXT NOT NULL,
                occurred_at  TEXT NOT NULL,
                type         TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            """;
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }

  private static void ApplyV3(SqliteConnection connection)
  {
    string sql = """
            CREATE TABLE IF NOT EXISTS learned_skills (
                name TEXT PRIMARY KEY,
                description TEXT NOT NULL,
                body TEXT NOT NULL,
                version INTEGER NOT NULL,
                provenance_session TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS skill_versions (
                name TEXT NOT NULL,
                version INTEGER NOT NULL,
                description TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (name, version)
            );
            CREATE TABLE IF NOT EXISTS skill_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                skill_name TEXT NOT NULL,
                viewed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_skill_usage_name ON skill_usage (skill_name);
            """;
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }

  private static void ApplyV4(SqliteConnection connection)
  {
    string sql = """
            CREATE TABLE IF NOT EXISTS curated_memories (
                id            TEXT PRIMARY KEY,
                workspace_id  TEXT NOT NULL,
                category      TEXT NOT NULL,
                tags          TEXT NOT NULL,
                content       TEXT NOT NULL,
                usage_hint    TEXT NULL,
                scope         TEXT NOT NULL,
                provenance    TEXT NULL,
                version       INTEGER NOT NULL,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_curated_ws ON curated_memories (workspace_id, scope);
            CREATE VIRTUAL TABLE IF NOT EXISTS curated_memories_fts USING fts5(
                content, tags, usage_hint, content='curated_memories', content_rowid='rowid'
            );
            CREATE TRIGGER IF NOT EXISTS curated_ai AFTER INSERT ON curated_memories BEGIN
                INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)
                VALUES (new.rowid, new.content, new.tags, new.usage_hint);
            END;
            CREATE TRIGGER IF NOT EXISTS curated_ad AFTER DELETE ON curated_memories BEGIN
                INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)
                VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);
            END;
            CREATE TRIGGER IF NOT EXISTS curated_au AFTER UPDATE ON curated_memories BEGIN
                INSERT INTO curated_memories_fts(curated_memories_fts, rowid, content, tags, usage_hint)
                VALUES ('delete', old.rowid, old.content, old.tags, old.usage_hint);
                INSERT INTO curated_memories_fts(rowid, content, tags, usage_hint)
                VALUES (new.rowid, new.content, new.tags, new.usage_hint);
            END;
            """;
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }

  private static void ApplyV5(SqliteConnection connection)
  {
    string sql = """
            CREATE VIRTUAL TABLE IF NOT EXISTS state_keys_fts USING fts5(
                value, ns, name, content='state_keys', content_rowid='rowid'
            );
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
            INSERT INTO state_keys_fts(rowid, value, ns, name)
                SELECT rowid, value, ns, name FROM state_keys;
            """;
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }
  private static void ApplyV6(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS provider_exclusions (
            model_provider_key TEXT NOT NULL,
            workspace_id       TEXT NOT NULL,
            expires_at         TEXT NOT NULL,
            created_at         TEXT NOT NULL,
            PRIMARY KEY (model_provider_key, workspace_id)
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private static void ApplyV7(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS app_preferences (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  // Root agent rows gain their workspace binding and provider so sessions can be
  // listed per workspace and resumed by id. Discovery metadata only — conversation
  // content stays keyed by agent id. NULL for legacy rows (not resumable) and for
  // spawned children (they inherit their root's workspace).
  private static void ApplyV8(SqliteConnection connection)
  {
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'workspace_id';",
        "ALTER TABLE agents ADD COLUMN workspace_id TEXT NULL;");
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'provider';",
        "ALTER TABLE agents ADD COLUMN provider TEXT NULL;");
    string sql = """
        CREATE INDEX IF NOT EXISTS ix_agents_ws_created ON agents (workspace_id, created_at);
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>SQLite has no ADD COLUMN IF NOT EXISTS; the catalog check makes the one
  ///     non-idempotent migration step safe to re-run — a pass that read a stale
  ///     user_version must not fail on a column a concurrent pass already added.
  ///     DDL identifiers cannot be bound parameters, so both statements arrive as
  ///     whole, call-site-constant command texts — never composed from input.</summary>
  private static void AddColumnIfMissing(SqliteConnection connection, string checkSql, string alterSql)
  {
    using SqliteCommand check = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    check.CommandText = checkSql;
#pragma warning restore CA2100
    if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
    {
      using SqliteCommand alter = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
      alter.CommandText = alterSql;
#pragma warning restore CA2100
      _ = alter.ExecuteNonQuery();
    }
  }

  /// <summary>V9 adds watchdog_events: the append-only audit trail of watchdog decisions
  ///     (hung detections, wrap-up retries, deferrals, terminal reports, RSS breaches,
  ///     internal errors). Indexed for the two read shapes: per-agent attempt counting and
  ///     recent-event listing.</summary>
  private static void ApplyV9(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS watchdog_events (
            id          TEXT PRIMARY KEY,
            agent_id    TEXT,
            kind        TEXT NOT NULL,
            detail      TEXT NOT NULL,
            attempt     INTEGER NOT NULL,
            rss_mb      REAL,
            created_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_watchdog_events_agent_kind ON watchdog_events (agent_id, kind);
        CREATE INDEX IF NOT EXISTS ix_watchdog_events_created ON watchdog_events (created_at);
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }
  /// <summary>V10 gives agent rows runtime-owned facts (FR-L2): the wrap-up attempt count,
  ///     the running phase, and the serialized spawn contract. Legacy rows read defaults
  ///     (0 / null / null) through the AddColumnIfMissing defaults.</summary>
  private static void ApplyV10(SqliteConnection connection)
  {
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'attempts';",
        "ALTER TABLE agents ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0;");
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'phase';",
        "ALTER TABLE agents ADD COLUMN phase INTEGER NULL;");
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name = 'contract';",
        "ALTER TABLE agents ADD COLUMN contract TEXT NULL;");
  }

  /// <summary>V11 adds mailbox_messages: between-turn durability for undelivered mailbox
  ///     messages (FR-C5). Delivery is runtime-push only — the table is never polled (A1);
  ///     it exists so a settled child's unread steering survives to its next start.</summary>
  private static void ApplyV11(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS mailbox_messages (
            agent_id   TEXT NOT NULL,
            seq        INTEGER NOT NULL,
            sender     TEXT NOT NULL,
            urgency    INTEGER NOT NULL,
            text       TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (agent_id, seq)
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }
}
