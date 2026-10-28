using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #279: V20 repairs the corrupt rows a real workspace carried ('Completed'
///     written into a step-status column) and then refuses to let one back in. The repair
///     is pinned end-to-end: a legacy-shaped database with the exact corruption upgrades,
///     every plan becomes readable, and the statuses read as the step vocabulary.</summary>
public sealed class PlanStepStatusRepairMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-planv20-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, rolls plans/plan_steps back to their V13
  ///     (pre-CHECK) shape, seeds the #279 corruption, and stamps user_version 19.</summary>
  private void SeedV19WithCorruptRows()
  {
    _ = new AppDatabase(_dbPath);
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE plans_old (
            id INTEGER PRIMARY KEY AUTOINCREMENT, workspace_id TEXT NOT NULL, session_id TEXT NOT NULL,
            title TEXT NOT NULL, goal TEXT NOT NULL, status TEXT NOT NULL, version INTEGER NOT NULL,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE plan_steps_old (
            plan_id INTEGER NOT NULL REFERENCES plans(id), position INTEGER NOT NULL, title TEXT NOT NULL,
            detail TEXT, todo_id INTEGER, status TEXT NOT NULL, PRIMARY KEY (plan_id, position));
        DROP TABLE plan_steps;
        DROP TABLE plans;
        ALTER TABLE plans_old RENAME TO plans;
        ALTER TABLE plan_steps_old RENAME TO plan_steps;

        INSERT INTO plans (id, workspace_id, session_id, title, goal, status, version, created_at, updated_at)
        VALUES (30, 'C:\ws\alpha', 's', 'corrupt plan', 'g', 'Active', 2, '2026-10-24T00:00:00.0000000+00:00', '2026-10-24T00:00:00.0000000+00:00'),
               (31, 'C:\ws\alpha', 's', 'healthy plan', 'g', 'Active', 1, '2026-10-24T00:00:00.0000000+00:00', '2026-10-24T00:00:00.0000000+00:00');
        INSERT INTO plan_steps (plan_id, position, title, detail, todo_id, status) VALUES
            (30, 1, 'a', NULL, NULL, 'Completed'),
            (30, 2, 'b', NULL, NULL, 'Completed'),
            (31, 1, 'c', NULL, NULL, 'Pending'),
            (31, 2, 'd', NULL, NULL, 'Done');
        PRAGMA user_version = 19;
        """;
    _ = command.ExecuteNonQuery();
  }

  [Fact]
  public async Task V20_RepairsCompletedStepRowsToDone_AndMakesEveryPlanReadable()
  {
    SeedV19WithCorruptRows();

    AppDatabase db = new(_dbPath); // constructor migrates to V20
    SqlitePlanStore store = new(db, "C:\\ws\\alpha");

    Result<PlanCatalog> listed = await store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "the listing must succeed");
    Assert.Empty(listed.Value.Corrupt);
    Assert.Equal([30, 31], listed.Value.Plans.Select(p => p.Id));
    Plan repaired = listed.Value.Plans.Single(p => p.Id == 30);
    Assert.All(repaired.Steps, s => Assert.Equal(PlanStepStatus.Done, s.Status));
    // Ids and versions survive the table rebuild: no plan changes identity or CAS state.
    Assert.Equal(2, repaired.Version);
  }

  [Fact]
  public async Task V20_IsIdempotent_ReopeningKeepsTheRepairedRows()
  {
    SeedV19WithCorruptRows();
    _ = new AppDatabase(_dbPath);
    _ = new AppDatabase(_dbPath); // reopen: no-op

    SqlitePlanStore store = new(new AppDatabase(_dbPath), "C:\\ws\\alpha");
    Result<PlanCatalog> listed = await store.ListAsync(null, TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected success");
    Assert.Equal(2, listed.Value.Plans.Count);
    Assert.Empty(listed.Value.Corrupt);
  }

  [Fact]
  public void V20_LeavesNoOrphanTables_AndKeepsTheCheckConstraints()
  {
    SeedV19WithCorruptRows();
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('plans_old','plan_steps_old','plans_v20','plan_steps_v20','plan_steps_v21');"));
    string sql = (string)Scalar(connection, "SELECT sql FROM sqlite_master WHERE name='plan_steps';");
    Assert.Contains("CHECK", sql, StringComparison.Ordinal);
  }

  /// <summary>Issue #271: V21 widens the step-status CHECK to admit 'Skipped', and only
  ///     that - an out-of-enum value is still refused by SQLite itself.</summary>
  [Fact]
  public async Task V21_AdmitsSkippedStepStatus_AndStillRefusesOutOfEnumValues()
  {
    SeedV19WithCorruptRows();
    AppDatabase db = new(_dbPath); // migrates through V20 and V21
    SqlitePlanStore store = new(db, "C:\\ws\\alpha");

    Result<PlanCatalog> listed = await store.ListAsync(null, TestContext.Current.CancellationToken);
    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected the listing to succeed");
    Plan healthy = listed.Value.Plans.Single(p => p.Id == 31);
    Result<Plan> saved = await store.SaveAsync(
        healthy.UpdateStep(1, s => s with { Status = PlanStepStatus.Skipped }),
        healthy.Version, TestContext.Current.CancellationToken);
    Assert.True(saved.IsSuccess, saved.Error?.Message ?? "expected Skipped to be admitted");

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    // The CHECK is still a closed set: only the four step statuses are storable.
    _ = Assert.Throws<SqliteException>(() => Scalar(connection,
        "UPDATE plan_steps SET status = 'Completed' WHERE plan_id = 31 AND position = 2;"));
  }

  /// <summary>Issue #310: V24 drops the retired <c>todo_id</c> column and carries the
  ///     step-status CHECK forward verbatim. Both halves are pinned here, because the
  ///     rebuild is the one place a constraint could silently be lost.</summary>
  [Fact]
  public async Task V24_DropsTodoId_KeepsRowsAndTheStatusCheck()
  {
    SeedV19WithCorruptRows();
    AppDatabase db = new(_dbPath); // migrates through V20, V21 and V24
    SqlitePlanStore store = new(db, "C:\\ws\\alpha");

    Result<PlanCatalog> listed = await store.ListAsync(null, TestContext.Current.CancellationToken);
    Assert.True(listed.IsSuccess, listed.Error?.Message ?? "expected the listing to succeed");
    // Every row survived the rebuild: the plan ids and their steps are all still there.
    Assert.Equal([30, 31], listed.Value.Plans.Select(p => p.Id));
    Assert.Equal(2, listed.Value.Plans.Single(p => p.Id == 31).Steps.Count);

    // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
    await using SqliteConnection connection = new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(0L, Scalar(connection,
        "SELECT COUNT(*) FROM pragma_table_info('plan_steps') WHERE name='todo_id';"));
    string sql = (string)Scalar(connection, "SELECT sql FROM sqlite_master WHERE name='plan_steps';");
    Assert.Contains("CHECK", sql, StringComparison.Ordinal);
    Assert.Contains("Skipped", sql, StringComparison.Ordinal);
    // The constraint is still a closed set after the rebuild.
    _ = Assert.Throws<SqliteException>(() => Scalar(connection,
        "UPDATE plan_steps SET status = 'Completed' WHERE plan_id = 31 AND position = 2;"));
    // The rebuild leaves no scaffolding behind.
    Assert.Equal(0L, Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE name='plan_steps_v24';"));
  }

  private static object Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return command.ExecuteScalar()!;
  }
}
