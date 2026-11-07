using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #334: V32 retires the parent-child link from <c>agents</c> without losing
///     history. Pinned end-to-end - a pre-V32 database upgrades in place, the columns keep
///     their historical values verbatim, <c>depth</c> loses its NOT NULL constraint so a row
///     with no depth can be written, and every row still loads through the production store.
///     <para>WHY THIS EXISTS: V32 is exercised by every fixture that inserts an agent, but
///     being exercised is not the same as being pinned. A later change that re-declared
///     <c>depth</c> NOT NULL would fail only in a live session; the assertion here fails in
///     the suite.</para></summary>
public sealed class AgentParentLinkRetirementMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = TemplateDatabase.Copy();

  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

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

  /// <summary>Strips <c>agents</c> back to the V2 shape and stamps user_version 31 - the exact
  ///     pre-V32 state a shipped database is in.
  ///     <para>THE PRE-V32 VERSION IS A LITERAL, deliberately: 'latest minus one' would silently
  ///     change meaning the moment a V33 exists, and this fixture must reproduce V31 forever.
  ///     </para></summary>
  private void SeedV31(string parentId, string childId)
  {
    Execute($$"""
      PRAGMA foreign_keys = OFF;
      CREATE TABLE agents_v31 (
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
          final_report   TEXT NULL,
          workspace_id   TEXT NULL,
          provider       TEXT NULL,
          attempts       INTEGER NOT NULL DEFAULT 0,
          phase          INTEGER NULL,
          contract       TEXT NULL
      );
      INSERT INTO agents_v31 (id, parent_id, depth, status, failure_reason, model_used, label,
          task_prompt, created_at, completed_at, final_report, workspace_id, provider, attempts,
          phase, contract)
          SELECT id, parent_id, depth, status, failure_reason, model_used, label, task_prompt,
              created_at, completed_at, final_report, workspace_id, provider, attempts, phase,
              contract FROM agents;
      DROP TABLE agents;
      ALTER TABLE agents_v31 RENAME TO agents;
      INSERT INTO agents (id, parent_id, depth, status, model_used, label, task_prompt,
          created_at, workspace_id, provider)
          VALUES ('{{parentId}}', NULL, 0, 0, 'openrouter/auto', 'root', 'root prompt',
              '2026-10-30T09:00:00.0000000+00:00', '{{Ws}}', 'openrouter');
      INSERT INTO agents (id, parent_id, depth, status, model_used, label, task_prompt,
          created_at, workspace_id, provider)
          VALUES ('{{childId}}', '{{parentId}}', 1, 0, 'mock/sub-model', 'child', 'task',
              '2026-10-30T09:05:00.0000000+00:00', '{{Ws}}', 'openrouter');
      PRAGMA user_version = 31;
      PRAGMA foreign_keys = ON;
      """);
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
    object? value = command.ExecuteScalar();
    Assert.NotNull(value);
    return (T)value;
  }

  [Fact]
  public void V32_KeepsTheHistoricalColumns_AndDropsTheNotNullOnDepth()
  {
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    // The columns survive as historical columns: the era baseline reads them.
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='parent_id';"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('agents') WHERE name='depth';"));
    // ... and depth is OPTIONAL now, so a row this build writes needs no depth at all.
    Assert.Equal(0L, Scalar<long>(
        "SELECT \"notnull\" FROM pragma_table_info('agents') WHERE name='depth';"));
  }

  [Fact]
  public void V32_UpgradesAV31Database_KeepingHistoricalParentAndDepthVerbatim()
  {
    string parentId = Guid.NewGuid().ToString();
    string childId = Guid.NewGuid().ToString();
    _ = new AppDatabase(_dbPath);
    SeedV31(parentId, childId);

    _ = new AppDatabase(_dbPath);

    Assert.Equal(parentId, Scalar<string>(
        $"SELECT parent_id FROM agents WHERE id='{childId}';"));
    Assert.Equal(1L, Scalar<long>(
        $"SELECT depth FROM agents WHERE id='{childId}';"));
    Assert.Equal(0L, Scalar<long>(
        $"SELECT depth FROM agents WHERE id='{parentId}';"));
    Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM agents;"));
    // The rebuild leaves no orphan table behind.
    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agents_v32';"));
  }

  [Fact]
  public async Task V32_ExistingRows_StillLoadThroughTheStore_AndANewRowWritesNoDepth()
  {
    string parentId = Guid.NewGuid().ToString();
    string childId = Guid.NewGuid().ToString();
    _ = new AppDatabase(_dbPath);
    SeedV31(parentId, childId);

    AppDatabase database = new(_dbPath);
    SqliteAgentStore store = new(database);

    Result<AgentRecord> historical = await store.GetAsync(new AgentId(Guid.Parse(childId)),
        TestContext.Current.CancellationToken);
    Assert.True(historical.IsSuccess, historical.Error?.Message ?? "a historical row must load");
    Assert.Equal("child", historical.Value.Label);

    // A row this build writes carries no parent and no depth: the INSERT omits both columns,
    // which only succeeds because V32 relaxed depth's NOT NULL.
    AgentRecord fresh = AgentRecord.Started(AgentId.NewId(), "openrouter/auto", "new", "task", T0,
        workspaceId: Ws, provider: "openrouter");
    Result<string> saved = await store.SaveAsync(fresh, ct: TestContext.Current.CancellationToken);
    Assert.True(saved.IsSuccess, saved.Error?.Message ?? "a row with no depth must save");

    Result<AgentRecord> reloaded = await store.GetAsync(fresh.Id, TestContext.Current.CancellationToken);
    Assert.True(reloaded.IsSuccess);
    Assert.Equal(fresh.Id, reloaded.Value.Id);

    Assert.Equal(0L, Scalar<long>(
        $"SELECT COUNT(*) FROM agents WHERE id='{fresh.Id.Value}' AND depth IS NOT NULL;"));
  }
}
