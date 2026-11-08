using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>V37 drops the two model columns a chain step used to carry. Pinned end-to-end: an
///     existing V36 database upgrades in place with its rows intact, the columns are gone from
///     the table, and the rebuild leaves no orphan table.
///     <para>A STEP CARRIES NO MODEL. Its session runs on the model the workspace has selected,
///     so a per-step model was a second place a model choice could be made.</para></summary>
public sealed class ChainStepModelColumnsRemovalMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
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

  /// <summary>Builds a fully-migrated database, restores the V36 shape of
  ///     <c>chain_steps</c> - the two model columns back - and stamps user_version 36, the exact
  ///     pre-V37 state a shipped database is in.</summary>
  private void SeedV36()
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v36 (
            chain_id INTEGER NOT NULL REFERENCES chains(id),
            position INTEGER NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Running', 'Done', 'Failed', 'Skipped', 'Parked')),
            session_id TEXT,
            attempts INTEGER NOT NULL,
            outcome TEXT,
            actor TEXT NOT NULL,
            claimed_at TEXT,
            completed_at TEXT,
            role TEXT NOT NULL DEFAULT 'Implement' CHECK (role IN ('Implement', 'Review')),
            role_model TEXT,
            served_model TEXT,
            review_attempts INTEGER NOT NULL DEFAULT 0 CHECK (review_attempts >= 0),
            invalidated_by TEXT,
            group_id INTEGER,
            worktree_path TEXT,
            worktree_branch TEXT,
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v36 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model, review_attempts,
            invalidated_by, group_id, worktree_path, worktree_branch)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, NULL, NULL, review_attempts, invalidated_by, group_id,
                worktree_path, worktree_branch FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v36 RENAME TO chain_steps;
        PRAGMA user_version = 36;
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
    object value = command.ExecuteScalar()
        ?? throw new InvalidOperationException("the query returned no value");
    return (T)value;
  }

  [Fact]
  public void V37_DropsBothModelColumns_AndStampsTheLatestVersion()
  {
    SeedV36();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('chain_steps') WHERE name IN ('role_model', 'served_model');"));
  }

  [Fact]
  public void V37_KeepsEveryRowAndEveryExecutionColumn()
  {
    SeedV36();
    Execute("""
        INSERT INTO chains (workspace_id, plan_id, plan_version, title, status, park_reason,
            parked_at_position, session_id, created_at, updated_at, version)
            VALUES ('C:\\ws\\alpha', 1, 1, 'legacy', 'Running', NULL, NULL, NULL,
                '2026-10-29T00:00:00.0000000+00:00', '2026-10-29T00:00:00.0000000+00:00', 1);
        INSERT INTO chain_steps (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model, review_attempts)
            VALUES (1, 2, 'Done', 'session-a', 1, 'judged', 'session:session-a', NULL,
                '2026-10-29T00:00:00.0000000+00:00', 'Review', 'vendor/reviewer', 'vendor/reviewer', 3);
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal("Review", Scalar<string>("SELECT role FROM chain_steps WHERE position=2;"));
    Assert.Equal("Done", Scalar<string>("SELECT status FROM chain_steps WHERE position=2;"));
    Assert.Equal("judged", Scalar<string>("SELECT outcome FROM chain_steps WHERE position=2;"));
    Assert.Equal(3L, Scalar<long>("SELECT review_attempts FROM chain_steps WHERE position=2;"));
  }

  [Fact]
  public void V37_KeepsTheClosedSetConstraints()
  {
    SeedV36();
    _ = new AppDatabase(_dbPath);

    string stepsSql = Scalar<string>(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='chain_steps';");
    Assert.Contains("CHECK (role IN ('Implement', 'Review'))", stepsSql, StringComparison.Ordinal);
    Assert.Contains("'Skipped'", stepsSql, StringComparison.Ordinal);
  }

  [Fact]
  public void V37_LeavesNoOrphanTables()
  {
    SeedV36();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v37' OR name LIKE '%_old';"));
  }

  [Fact]
  public async Task V37_ARoundTrippedReviewStepCarriesNoModel()
  {
    SeedV36();
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    Result<Chain> created = await store.CreateAsync(
        Chain.NewFromSeeds(Ws, 5, 1, "reviewed", null,
            [ChainStepSeed.Implementing(1), new ChainStepSeed(2, ChainStepRole.Review)],
            DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "create must succeed");

    Result<Chain> reloaded = await store.GetAsync(created.Value.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(reloaded.IsSuccess, reloaded.Error?.Message ?? "get must succeed");
    Assert.Equal(ChainStepRoleKind.Implement, reloaded.Value.Steps[0].Role.Kind);
    Assert.Equal(ChainStepRoleKind.Review, reloaded.Value.Steps[1].Role.Kind);
  }
}
