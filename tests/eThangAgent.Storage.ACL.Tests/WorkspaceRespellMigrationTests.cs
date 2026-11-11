using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #374: V39 collapses the two spellings of one directory that the pre-#374 store
///     boundary let through, so a workspace's records stop being invisible to half its sessions.
///     <para>Pinned end-to-end because it is the one step that moves rows that already exist:
///     the fork is repaired in place, in the order that keeps every reference intact (records
///     first, the identity table last), and a collision keeps the row the store's own semantics
///     say it should.</para></summary>
public sealed class WorkspaceRespellMigrationTests : IDisposable
{
  private const string Normalized = "C:\\ws\\alpha";
  private const string Respelled = "C:\\ws\\alpha\\";
  private readonly string _dbPath = TemplateDatabase.Copy();

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

  /// <summary>Builds a fully-migrated database and stamps user_version 38 - the exact pre-V39
  ///     state a shipped database is in.</summary>
  private void SeedV38()
  {
    _ = new AppDatabase(_dbPath);
    Execute("PRAGMA user_version = 38;");
  }

  private void Execute(string sql)
  {
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  private T Scalar<T>(string sql)
  {
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    object value = command.ExecuteScalar()
        ?? throw new InvalidOperationException("the query returned no value");
    return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
  }

  [Fact]
  public void Migration_MovesEveryRecordOntoTheNormalizedSpelling()
  {
    SeedV38();
    // Raw string literals do NOT process escapes, so these carry single backslashes.
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\ws\alpha\', 's1', 'Plan', 'Goal', 'Active', 1, '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z');
        INSERT INTO chains (workspace_id, plan_id, plan_version, title, status, created_at, updated_at, version)
            VALUES ('C:\ws\alpha\', 1, 1, 'Chain', 'Running', '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z', 1);
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\alpha\', 'goal', 'check', '[]', 1, '2026-10-28T00:00:00Z');
        INSERT INTO agents (id, parent_id, depth, model_used, status, task_prompt, created_at, workspace_id, provider)
            VALUES ('a1', NULL, 0, 'm', 0, 'prompt', '2026-10-28T00:00:00Z', 'C:\ws\alpha\', 'openrouter');
        INSERT INTO workspace_identities (id, root, bound_at)
            VALUES ('C:\ws\alpha\', 'C:\ws\alpha\', '2026-10-28T00:00:00Z');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(0L, Scalar<long>($"SELECT COUNT(*) FROM plans WHERE workspace_id = '{Respelled}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM plans WHERE workspace_id = '{Normalized}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM chains WHERE workspace_id = '{Normalized}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM state_keys WHERE workspace_id = '{Normalized}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM agents WHERE workspace_id = '{Normalized}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM workspace_identities WHERE id = '{Normalized}' AND root = '{Normalized}';"));
    Assert.Equal(0L, Scalar<long>($"SELECT COUNT(*) FROM workspace_identities WHERE id = '{Respelled}' OR root = '{Respelled}';"));
  }

  /// <summary>The chain and the plan it executes must end up on the SAME spelling, or the chain
  ///     step that just finished still cannot mark its step done - the defect itself.</summary>
  [Fact]
  public void Migration_LeavesAChainAndItsPlanOnOneSpelling()
  {
    SeedV38();
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\ws\alpha', 's1', 'Plan', 'Goal', 'Active', 1, '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z');
        INSERT INTO chains (workspace_id, plan_id, plan_version, title, status, created_at, updated_at, version)
            VALUES ('C:\ws\alpha\', 1, 1, 'Chain', 'Running', '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z', 1);
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>("""
        SELECT COUNT(*) FROM chains c JOIN plans p ON p.id = c.plan_id
            WHERE c.workspace_id = p.workspace_id;
        """));
  }

  /// <summary>A key holds its LAST WRITE, so the later-stamped fork is the value the merged key
  ///     keeps - and the row count proves the two became one.</summary>
  [Fact]
  public void Migration_OfAForkedStateKey_KeepsTheLaterWrite()
  {
    SeedV38();
    Execute("""
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\alpha', 'sci.plan', 'ledger', 'earlier', 1, '2026-10-28T00:00:00Z');
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\alpha\', 'sci.plan', 'ledger', 'later', 7, '2026-11-08T00:00:00Z');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM state_keys WHERE ns = 'sci.plan';"));
    Assert.Equal("later", Scalar<string>("SELECT value FROM state_keys WHERE ns = 'sci.plan';"));
    Assert.Equal(7L, Scalar<long>("SELECT version FROM state_keys WHERE ns = 'sci.plan';"));
  }

  /// <summary>The spend read matches the usage payload's own stamp, so the stamp has to move with
  ///     the row or the spend stays attributed to a spelling that no longer exists.</summary>
  [Fact]
  public void Migration_RepairsTheUsagePayloadStamp()
  {
    SeedV38();
    Execute("""
        INSERT INTO agent_events (agent_id, occurred_at, type, payload_json)
            VALUES ('a1', '2026-10-28T00:00:00Z', 'usage', '{"WorkspaceId":"C:\\ws\\alpha\\","PromptTokens":1}');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM agent_events WHERE json_extract(payload_json, '$.WorkspaceId') = 'C:\\ws\\alpha';"));
  }

  /// <summary>V39 repaired the rows that existed when it ran, and the create path kept minting
  ///     new ones afterwards - a session opened from a folder picker stamped the raw path, so
  ///     the fork reopened on every open. The repair therefore runs again, over a database that
  ///     is already at V40 and holds rows V39 could not have seen.</summary>
  [Fact]
  public void Migration_RepairsAForkThatReopenedAfterV39Ran()
  {
    _ = new AppDatabase(_dbPath);
    Execute("PRAGMA user_version = 40;");
    Execute("""
        INSERT INTO agents (id, model_used, status, task_prompt, created_at, workspace_id, provider)
            VALUES ('a2', 'm', 0, 'prompt', '2026-11-10T00:00:00Z', 'C:\ws\alpha\', 'openrouter');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(0L, Scalar<long>($"SELECT COUNT(*) FROM agents WHERE workspace_id = '{Respelled}';"));
    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM agents WHERE workspace_id = '{Normalized}' AND id = 'a2';"));
  }

  /// <summary>A database that never forked is left exactly as it was: the step finds no id that
  ///     differs from its normalized form and writes nothing.</summary>
  [Fact]
  public void Migration_OfAnAlreadyNormalizedDatabase_ChangesNothing()
  {
    SeedV38();
    Execute("""
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\alpha', 'goal', 'check', '[]', 3, '2026-10-28T00:00:00Z');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(3L, Scalar<long>("SELECT version FROM state_keys WHERE ns = 'goal';"));
    Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM state_keys;"));
  }
}
