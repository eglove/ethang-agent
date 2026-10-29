using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #317 D5: V25 adds <c>workspace_identities</c>, the table that turns
///     <c>workspace_id</c> from a directory PATH into a stable IDENTITY.
///     <para>The migration is pinned end-to-end because it is the one step that must not lose a
///     workspace: an existing database upgrades in place, and the BACKFILL is what makes every
///     pre-#317 workspace keep working - each distinct <c>workspace_id</c> is bound to itself as
///     its root, so no record moves and no id is re-minted. A backfill that minted fresh ids
///     instead would orphan every plan, chain, state key, and session in the database.</para>
///     <para>Both columns are UNIQUE: two identities must never claim one directory (D6), and one
///     identity must never claim two (D5).</para></summary>
public sealed class WorkspaceIdentityMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-wsv25-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, drops V25's table and stamps user_version 24 -
  ///     the exact pre-V25 state a shipped database is in.</summary>
  private void SeedV24()
  {
    _ = new AppDatabase(_dbPath);
    Execute("DROP TABLE IF EXISTS workspace_identities; PRAGMA user_version = 24;");
  }

  [Fact]
  public void Migration_CreatesTheIdentityTable()
  {
    SeedV24();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='workspace_identities';"));
  }

  [Fact]
  public void Migration_BackfillsEveryExistingWorkspaceId_AsItsOwnRoot()
  {
    // The load-bearing half: a pre-#317 workspace's id IS its path, so binding it to itself
    // keeps every record where it is.
    SeedV24();
    // Raw string literals do NOT process escapes, so these are single backslashes - the same
    // form the assertions below spell in ordinary strings.
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\ws\alpha', 's1', 'Plan', 'Goal', 'Active', 1, '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z');
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\beta', 'goal', 'check', '[]', 1, '2026-10-28T00:00:00Z');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM workspace_identities;"));
    Assert.Equal("C:\\ws\\alpha", Scalar<string>(
        "SELECT root FROM workspace_identities WHERE id = 'C:\\ws\\alpha';"));
    Assert.Equal("C:\\ws\\beta", Scalar<string>(
        "SELECT root FROM workspace_identities WHERE id = 'C:\\ws\\beta';"));
  }

  [Fact]
  public void Migration_BindsEachIdOnce_EvenWhenSeveralTablesShareIt()
  {
    // plans, chains, state_keys, and agents all carry the same workspace_id. INSERT OR IGNORE
    // is what keeps one workspace from becoming four identity rows.
    SeedV24();
    Execute("""
        INSERT INTO plans (workspace_id, session_id, title, goal, status, version, created_at, updated_at)
            VALUES ('C:\ws\alpha', 's1', 'Plan', 'Goal', 'Active', 1, '2026-10-28T00:00:00Z', '2026-10-28T00:00:00Z');
        INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at)
            VALUES ('C:\ws\alpha', 'goal', 'check', '[]', 1, '2026-10-28T00:00:00Z');
        INSERT INTO agents (id, parent_id, depth, model_used, status, task_prompt, created_at, workspace_id, provider)
            VALUES ('a1', NULL, 0, 'm', 0, 'prompt', '2026-10-28T00:00:00Z', 'C:\ws\alpha', 'openrouter');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar<long>("SELECT COUNT(*) FROM workspace_identities;"));
  }

  [Fact]
  public void Migration_RefusesTwoIdentitiesOnOneRoot()
  {
    // D6: a copied directory must never share the original's record set, so the schema itself
    // refuses the state that would let it.
    _ = new AppDatabase(_dbPath);
    Execute("INSERT INTO workspace_identities (id, root, bound_at) VALUES ('ws-a', 'C:\\ws\\one', 'x');");

    _ = Assert.Throws<SqliteException>(() => Execute(
        "INSERT INTO workspace_identities (id, root, bound_at) VALUES ('ws-b', 'C:\\ws\\one', 'x');"));
  }

  [Fact]
  public void SchemaVersion_IsTwentyFive()
  {
    _ = new AppDatabase(_dbPath);
    Assert.Equal(25, Scalar<int>("PRAGMA user_version;"));
  }
}
