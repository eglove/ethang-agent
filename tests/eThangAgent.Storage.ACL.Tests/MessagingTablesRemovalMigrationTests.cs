using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V30 (issue #316): agent-to-agent messaging is retired, so the two
///     tables it wrote go with it - <c>agent_links</c> (V12, consented named links) and
///     <c>mailbox_messages</c> (V11, between-turn durability for undelivered peer mail).
///     D2 is explicit that they are DROPPED rather than left dormant: a table nothing reads
///     or writes is a lie about capability, and the next session to find it would reasonably
///     assume the protocol still exists.
///     <para>V11 and V12 still CREATE their tables on a fresh database so both paths walk the
///     same history (the V4/V18 and V22 precedents); V30 is the only step that removes them,
///     so a fresh database and an upgraded one end in the same place.</para></summary>
public sealed class MessagingTablesRemovalMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-v30mig-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, recreates the V11/V12 shapes V30 removes,
  ///     seeds a row into each (so the drop is proven to take DATA as well as structure),
  ///     and stamps user_version 29 - the exact pre-V30 state.</summary>
  private void SeedV29Shape()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the SQL is a literal fixture, not user input.
#pragma warning disable CA2100
    command.CommandText = string.Join('\n',
        "CREATE TABLE agent_links (",
        "    workspace_id  TEXT NOT NULL,",
        "    name          TEXT NOT NULL,",
        "    container     TEXT NOT NULL,",
        "    agent_address TEXT NOT NULL,",
        "    created_at    TEXT NOT NULL,",
        "    PRIMARY KEY (workspace_id, name)",
        ");",
        "INSERT INTO agent_links (workspace_id, name, container, agent_address, created_at)",
        "VALUES ('ws-demo', 'reviewer', 'c1', '00000000-0000-0000-0000-000000000001', 't');",
        "CREATE TABLE mailbox_messages (",
        "    agent_id   TEXT NOT NULL,",
        "    seq        INTEGER NOT NULL,",
        "    sender     TEXT NOT NULL,",
        "    urgency    INTEGER NOT NULL,",
        "    text       TEXT NOT NULL,",
        "    created_at TEXT NOT NULL,",
        "    message_id TEXT NULL,",
        "    PRIMARY KEY (agent_id, seq)",
        ");",
        "INSERT INTO mailbox_messages (agent_id, seq, sender, urgency, text, created_at, message_id)",
        "VALUES ('a1', 1, 'parent', 0, 'steer', 't', NULL);",
        "PRAGMA user_version = 29;");
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  [Fact]
  public void V30_Drops_Both_Messaging_Tables_And_Leaves_Other_Tables_Untouched()
  {
    SeedV29Shape();

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(AppDatabase.LatestSchemaVersion, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_links';"));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mailbox_messages';"));
    // Every earlier table is untouched.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_preferences';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agents';"));
  }

  [Fact]
  public void V30_Is_Idempotent_Reopening_Is_A_NoOp()
  {
    SeedV29Shape();

    _ = new AppDatabase(_dbPath); // migrate once
    _ = new AppDatabase(_dbPath); // reopen: no-op

    using SqliteConnection connection = Open();
    Assert.Equal(AppDatabase.LatestSchemaVersion, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_links';"));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mailbox_messages';"));
  }

  [Fact]
  public void Fresh_Database_Ends_Without_Either_Messaging_Table()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    Assert.Equal(AppDatabase.LatestSchemaVersion, Version(connection));
    // V11/V12 create them and V30 drops them: a fresh database walks the same path an
    // existing one did and ends in the same place.
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_links';"));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mailbox_messages';"));
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
