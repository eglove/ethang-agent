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

  /// <summary>The resolved database file path (the host scratch/launch contract).
  ///     Empty when the database is in-memory.</summary>
  public string DatabasePath { get; }

  public AppDatabase(string? databasePath = null)
      : this(ResolvePath(databasePath), migrate: true)
  {
  }

  /// <summary>Opens an EXISTING app database WITHOUT migrating it: the app owns the
  ///     schema, and a GUEST that migrates takes a schema-level lock on a file the app
  ///     may be writing. On a busy database that lock times out and the migration read
  ///     fails with "database is locked" — which the conversation host swallowed, so every
  ///     root turn was refused with "this conversation host is not bound to a root
  ///     session" instead of the real cause (2026-10-22).
  ///     <para>A missing file is a NAMED error, never a silent creation: the caller that
  ///     owns the schema creates it (and migrates it) with the ordinary constructor.</para></summary>
  public static AppDatabase OpenExisting(string databasePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

    return File.Exists(databasePath)
        ? new AppDatabase(databasePath, migrate: false)
        : throw new InvalidOperationException(
            $"the app database '{databasePath}' does not exist; the owner creates it, a guest never does.");
  }

  /// <summary>The database file this instance talks to: the explicit path, else the
  ///     environment override, else the app-owned default location.</summary>
  private static string ResolvePath(string? databasePath)
    => databasePath
        ?? Environment.GetEnvironmentVariable("ETHANG_AGENT_DB")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "eThangAgent", "eThangAgent.db");

  private AppDatabase(string path, bool migrate)
  {
    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    SqliteConnectionStringBuilder builder = new() { DataSource = path };
    DatabasePath = path;
    _connectionString = builder.ToString();
    builder.Mode = SqliteOpenMode.ReadOnly;
    _readOnlyConnectionString = builder.ToString();
    if (migrate)
    {
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

  /// <summary>The ordered migration table: one entry per schema version, in application
  ///     order. The runner walks it top to bottom, re-reading user_version before each
  ///     step so a partially-migrated database resumes at exactly the right step.</summary>
  private static readonly (int Version, Action<SqliteConnection> Apply)[] MigrationSteps =
  [
    (1, ApplyV1),
    (2, ApplyV2),
    (3, ApplyV3),
    (4, ApplyV4),
    (5, ApplyV5),
    (6, ApplyV6),
    (7, ApplyV7),
    (8, ApplyV8),
    (9, ApplyV9),
    (10, ApplyV10),
    (11, ApplyV11),
    (12, ApplyV12),
    (13, ApplyV13),
    (14, ApplyV14),
    (15, ApplyV15),
    (16, ApplyV16),
    (17, ApplyV17),
    (18, ApplyV18),
    (19, ApplyV19),
    (20, ApplyV20),
    (21, ApplyV21),
  ];

  private void Migrate()
  {
    using SqliteConnection connection = Open();
    foreach ((int version, Action<SqliteConnection> apply) in MigrationSteps)
    {
      ApplyIfPending(connection, version, apply);
    }
  }

  private static void ApplyIfPending(SqliteConnection connection, int version, Action<SqliteConnection> apply)
  {
    if (GetVersion(connection) < version)
    {
      apply(connection);
      SetVersion(connection, version);
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

  /// <summary>V12 adds agent_links: consented named links persisted per workspace (W2).
  ///     Consent decisions survive restarts; revocation deletes the row; replace-by-name
  ///     semantics ride the (workspace_id, name) primary key.</summary>
  private static void ApplyV12(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS agent_links (
            workspace_id  TEXT NOT NULL,
            name          TEXT NOT NULL,
            container     TEXT NOT NULL,
            agent_address TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            PRIMARY KEY (workspace_id, name)
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>V13 adds plans + plan_steps: durable structured plans (one row per plan,
  ///     workspace-bound; steps ride (plan_id, position); statuses stored as their string
  ///     names; timestamps ISO-8601).</summary>
  private static void ApplyV13(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS plans (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            title TEXT NOT NULL,
            goal TEXT NOT NULL,
            status TEXT NOT NULL,
            version INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS plan_steps (
            plan_id INTEGER NOT NULL REFERENCES plans(id),
            position INTEGER NOT NULL,
            title TEXT NOT NULL,
            detail TEXT,
            todo_id INTEGER,
            status TEXT NOT NULL,
            PRIMARY KEY (plan_id, position)
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>V14 adds command_runs: completed !-command runs per workspace (the chat
  ///     command surface). Ids are GLOBAL autoincrement but every read filters on the
  ///     bound workspace, mirroring SqlitePlanStore's isolation; output is capped by the
  ///     writer, not the schema. Timestamps ISO-8601.</summary>
  private static void ApplyV14(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS command_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            command TEXT NOT NULL,
            exit_code INTEGER NOT NULL,
            timed_out INTEGER NOT NULL,
            output TEXT NOT NULL,
            ran_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_command_runs_ws_id ON command_runs(workspace_id, id);
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>V15 adds the MCP config tables (issue #103): mcp_servers carries server
  ///     configs with the scope split (null workspace = global), approval state, and
  ///     pinned version from day one so the trust flow needs no second migration;
  ///     mcp_oauth_tokens is keyed by server id so removing a server deletes its tokens
  ///     atomically. Name uniqueness is per scope: SQLite treats NULLs as distinct in
  ///     unique indexes, so two partial unique indexes pin the two scope shapes.</summary>
  private static void ApplyV15(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS mcp_servers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            transport TEXT NOT NULL,
            command_or_url TEXT NOT NULL,
            args_json TEXT NOT NULL,
            env_json TEXT NOT NULL,
            headers_json TEXT NOT NULL,
            workspace_id TEXT,
            approval_state TEXT NOT NULL,
            pinned_version TEXT,
            created_at TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_servers_scope_name
            ON mcp_servers(name) WHERE workspace_id IS NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_servers_ws_scope_name
            ON mcp_servers(workspace_id, name) WHERE workspace_id IS NOT NULL;
        CREATE TABLE IF NOT EXISTS mcp_oauth_tokens (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            server_id INTEGER NOT NULL REFERENCES mcp_servers(id) ON DELETE CASCADE,
            access_token TEXT NOT NULL,
            refresh_token TEXT,
            expires_at TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_mcp_oauth_tokens_server ON mcp_oauth_tokens(server_id);
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private static void ApplyV16(SqliteConnection connection)
  {
    // The ALTER is not idempotent (no IF NOT EXISTS for columns), and a database
    // rolled back to an earlier version for testing can already carry the column:
    // add it only when missing. Statement order matters: the base table must
    // exist before the column add, so the batch is assembled in order.
    using SqliteCommand columnCheck = connection.CreateCommand();
    columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('mcp_servers') WHERE name = 'gate_mode';";
    bool hasColumn = Convert.ToInt32(columnCheck.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    using SqliteCommand tableCheck = connection.CreateCommand();
    tableCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'mcp_servers';";
    bool hasTable = Convert.ToInt32(tableCheck.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    string sql = """
        CREATE TABLE IF NOT EXISTS mcp_decisions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            server_id INTEGER NOT NULL,
            decision TEXT NOT NULL,
            detail TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_mcp_decisions_server ON mcp_decisions(server_id, id);
        """;
    if (!hasColumn)
    {
      // A database stamped past V15 without the V15 tables (a migration renumbering
      // survivor): V15's IF NOT EXISTS never re-runs, so V16 self-heals the base
      // shape - WITH its partial unique indexes, so scope-name uniqueness survives
      // on such databases - before the column add.
      sql = "ALTER TABLE mcp_servers ADD COLUMN gate_mode TEXT NOT NULL DEFAULT 'none';" + "\n" + sql;
    }

    if (!hasTable)
    {
      sql = "CREATE TABLE IF NOT EXISTS mcp_servers (\n" +
          "    id INTEGER PRIMARY KEY AUTOINCREMENT,\n" +
          "    name TEXT NOT NULL,\n" +
          "    transport TEXT NOT NULL,\n" +
          "    command_or_url TEXT NOT NULL,\n" +
          "    args_json TEXT NOT NULL,\n" +
          "    env_json TEXT NOT NULL,\n" +
          "    headers_json TEXT NOT NULL,\n" +
          "    workspace_id TEXT,\n" +
          "    approval_state TEXT NOT NULL,\n" +
          "    pinned_version TEXT,\n" +
          "    created_at TEXT NOT NULL\n" +
          ");\n" +
          "CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_servers_scope_name ON mcp_servers(name) WHERE workspace_id IS NULL;\n" +
          "CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_servers_ws_scope_name ON mcp_servers(workspace_id, name) WHERE workspace_id IS NOT NULL;\n" +
          sql;
    }

    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>V17 adds the persisted provider model-catalog cache: one row per provider
  ///     holding the serialized catalog payload and the fetch timestamp, so the model
  ///     picker and smart-selection previews serve instantly from disk between app runs
  ///     and revalidate in the background. Provider-scoped (not workspace-scoped): the
  ///     catalog is one global list per provider, shared by every workspace.</summary>
  private static void ApplyV17(SqliteConnection connection)
  {
    string sql = """
        CREATE TABLE IF NOT EXISTS model_catalog_cache (
            provider     TEXT PRIMARY KEY,
            payload_json TEXT NOT NULL,
            fetched_at   TEXT NOT NULL
        );
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  /// <summary>V19 adds the message identity to mailbox_messages (issue #92): a persisted
  ///     remainder must stay ADDRESSABLE across the settle that persisted it, or a sender
  ///     could never learn what became of a message that outlived its run. Nullable by
  ///     necessity — rows written before this migration have no id, and the store mints one
  ///     when it loads them (a fabricated identity for an old row is still an honest
  ///     answer: the message exists and is undelivered; only its original handle is gone).</summary>
  private static void ApplyV19(SqliteConnection connection)
  {
    AddColumnIfMissing(connection,
        "SELECT COUNT(*) FROM pragma_table_info('mailbox_messages') WHERE name = 'message_id';",
        "ALTER TABLE mailbox_messages ADD COLUMN message_id TEXT NULL;");
  }

  /// <summary>V20 repairs the corrupt plan_steps rows and closes the door behind them
  ///     (issue #279). Six rows carried status 'Completed' - a PlanStatus value written into
  ///     a step-status column - which made the store throw while materializing the LIST, so
  ///     no plan in the workspace was readable. The repair maps the plan-level vocabulary
  ///     onto the step-level one ('Completed' -> 'Done') and stamps any other out-of-enum
  ///     value to 'Pending' rather than guessing a completed step: an unknown status is not
  ///     evidence that work finished. The CHECK constraints are what make this a one-time
  ///     repair - the capability provider already refuses bad input, but nothing stopped a
  ///     DIRECT writer (an exec script's SQL), and now SQLite itself refuses. The constraints
  ///     are added by rebuilding the tables, because SQLite cannot ALTER TABLE ADD CONSTRAINT;
  ///     ids and versions are preserved verbatim, so no plan changes identity or CAS state.</summary>
  private static void ApplyV20(SqliteConnection connection)
  {
    // The rebuild DROPS the parent table while children still reference it, which
    // SQLite refuses under foreign-key enforcement. Enforcement is a per-connection
    // pragma and cannot be toggled inside a transaction, so it is switched off around
    // the rebuild and restored after - the data is copied verbatim, so no FK can be
    // violated by the copy itself.
    Execute(connection, "PRAGMA foreign_keys = OFF;");
    try
    {
      RebuildPlanTables(connection);
    }
    finally
    {
      Execute(connection, "PRAGMA foreign_keys = ON;");
    }
  }

  private static void Execute(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private static void RebuildPlanTables(SqliteConnection connection)
  {
    string sql = """
        UPDATE plan_steps SET status = 'Done' WHERE status = 'Completed';
        UPDATE plan_steps SET status = 'Pending'
         WHERE status NOT IN ('Pending', 'InProgress', 'Done');
        UPDATE plans SET status = 'Abandoned'
         WHERE status NOT IN ('Active', 'Completed', 'Abandoned');

        CREATE TABLE plans_v20 (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            title TEXT NOT NULL,
            goal TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Active', 'Completed', 'Abandoned')),
            version INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        INSERT INTO plans_v20 (id, workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            SELECT id, workspace_id, session_id, title, goal, status, version, created_at, updated_at FROM plans;
        DROP TABLE plans;
        ALTER TABLE plans_v20 RENAME TO plans;

        CREATE TABLE plan_steps_v20 (
            plan_id INTEGER NOT NULL REFERENCES plans(id),
            position INTEGER NOT NULL,
            title TEXT NOT NULL,
            detail TEXT,
            todo_id INTEGER,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'InProgress', 'Done')),
            PRIMARY KEY (plan_id, position)
        );
        INSERT INTO plan_steps_v20 (plan_id, position, title, detail, todo_id, status)
            SELECT plan_id, position, title, detail, todo_id, status FROM plan_steps;
        DROP TABLE plan_steps;
        ALTER TABLE plan_steps_v20 RENAME TO plan_steps;
        """;
    using SqliteTransaction transaction = connection.BeginTransaction();
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    _ = command.ExecuteNonQuery();
    transaction.Commit();
  }

  /// <summary>V21 widens the plan_steps status CHECK to admit 'Skipped' (issue #271).
  ///     The completion invariant refuses to complete a plan while any step is Pending or
  ///     InProgress, and 'Skipped' is the truthful escape that keeps the refusal honest -
  ///     without it the cheapest way past the gate would be to mark unfinished work Done.
  ///     V20 had pinned the column to three values, so the constraint is rebuilt rather
  ///     than dropped: SQLite cannot ALTER a CHECK, and the door V20 closed (a DIRECT
  ///     writer smuggling an out-of-enum value) must stay closed. Rows, ids and versions
  ///     are copied verbatim; foreign-key enforcement is suspended around the swap for the
  ///     same reason V20 does it.</summary>
  private static void ApplyV21(SqliteConnection connection)
  {
    Execute(connection, "PRAGMA foreign_keys = OFF;");
    try
    {
      string sql = """
          CREATE TABLE plan_steps_v21 (
              plan_id INTEGER NOT NULL REFERENCES plans(id),
              position INTEGER NOT NULL,
              title TEXT NOT NULL,
              detail TEXT,
              todo_id INTEGER,
              status TEXT NOT NULL CHECK (status IN ('Pending', 'InProgress', 'Done', 'Skipped')),
              PRIMARY KEY (plan_id, position)
          );
          INSERT INTO plan_steps_v21 (plan_id, position, title, detail, todo_id, status)
              SELECT plan_id, position, title, detail, todo_id, status FROM plan_steps;
          DROP TABLE plan_steps;
          ALTER TABLE plan_steps_v21 RENAME TO plan_steps;
          """;
      using SqliteTransaction transaction = connection.BeginTransaction();
      using SqliteCommand command = connection.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = sql;
      _ = command.ExecuteNonQuery();
      transaction.Commit();
    }
    finally
    {
      Execute(connection, "PRAGMA foreign_keys = ON;");
    }
  }

  /// <summary>V18 removes the curated-memories feature (issue #236): the store, its
  ///     capability surface, and the turn-boundary nudge loop are gone, so the schema
  ///     goes with them. Drops the sync triggers first (a trigger on a dropped table
  ///     would otherwise fail), then the FTS5 index, the index, and the table. Fresh
  ///     databases run V4 then V18 and end clean; existing databases lose the rows —
  ///     no export is planned.</summary>
  private static void ApplyV18(SqliteConnection connection)
  {
    string sql = """
        DROP TRIGGER IF EXISTS curated_ai;
        DROP TRIGGER IF EXISTS curated_ad;
        DROP TRIGGER IF EXISTS curated_au;
        DROP TABLE IF EXISTS curated_memories_fts;
        DROP INDEX IF EXISTS ix_curated_ws;
        DROP TABLE IF EXISTS curated_memories;
        """;
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }
}
