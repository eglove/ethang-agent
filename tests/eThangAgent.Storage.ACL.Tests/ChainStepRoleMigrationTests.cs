using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #313 D7: V26 adds the chain step's role. Pinned end-to-end - an existing
///     V25 database upgrades in place with its rows intact, every pre-existing step becomes
///     an IMPLEMENTER step (there was no review step to have recorded), the role column is a
///     CHECK-constrained closed set, and the rebuild leaves no orphan table.</summary>
public sealed class ChainStepRoleMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-chainv26-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, strips the V26 columns back to the V25 shape,
  ///     and stamps user_version 25 - the exact pre-V26 state a shipped database is in.
  ///     <para>The table is REBUILT here rather than altered, because SQLite cannot drop a
  ///     column: the fixture must reproduce the old shape exactly, or the migration would be
  ///     tested against a database that never existed.</para></summary>
  private void SeedV25()
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v25 (
            chain_id INTEGER NOT NULL REFERENCES chains(id),
            position INTEGER NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Running', 'Done', 'Failed', 'Skipped', 'Parked')),
            session_id TEXT,
            attempts INTEGER NOT NULL,
            outcome TEXT,
            actor TEXT NOT NULL,
            claimed_at TEXT,
            completed_at TEXT,
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v25 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v25 RENAME TO chain_steps;
        PRAGMA user_version = 25;
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
  public void V26_AddsBothRoleColumns_AndStampsTheLatestVersion()
  {
    SeedV25();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('chain_steps') WHERE name='role';"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('chain_steps') WHERE name='role_model';"));
  }

  [Fact]
  public void V26_BackfillsEveryExistingStepAsAnImplementer()
  {
    SeedV25();
    // A pre-V26 chain with a step, written in the OLD shape (no role columns).
    Execute("""
        INSERT INTO chains (workspace_id, plan_id, plan_version, title, status, park_reason,
            parked_at_position, session_id, created_at, updated_at, version)
            VALUES ('C:\\ws\\alpha', 1, 1, 'legacy', 'Running', NULL, NULL, NULL,
                '2026-10-29T00:00:00.0000000+00:00', '2026-10-29T00:00:00.0000000+00:00', 1);
        INSERT INTO chain_steps (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at)
            VALUES (1, 1, 'Done', 'session-a', 1, 'shipped', 'session:session-a', NULL,
                '2026-10-29T00:00:00.0000000+00:00');
        """);

    _ = new AppDatabase(_dbPath);

    Assert.Equal("Implement", Scalar<string>(
        "SELECT role FROM chain_steps WHERE chain_id=1 AND position=1;"));
    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM chain_steps WHERE role_model IS NOT NULL;"));
    // The execution columns are copied VERBATIM: no chain changes identity or CAS state.
    Assert.Equal("Done", Scalar<string>(
        "SELECT status FROM chain_steps WHERE chain_id=1 AND position=1;"));
    Assert.Equal("shipped", Scalar<string>(
        "SELECT outcome FROM chain_steps WHERE chain_id=1 AND position=1;"));
  }

  [Fact]
  public void V26_RoleColumnIsAClosedSet()
  {
    SeedV25();
    _ = new AppDatabase(_dbPath);

    string stepsSql = Scalar<string>(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='chain_steps';");
    Assert.Contains("CHECK (role IN ('Implement', 'Review'))", stepsSql, StringComparison.Ordinal);
    // The V23 status CHECK is carried forward verbatim, so that door stays closed.
    Assert.Contains("'Skipped'", stepsSql, StringComparison.Ordinal);
  }

  [Fact]
  public void V26_LeavesNoOrphanTables()
  {
    SeedV25();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v26' OR name LIKE '%_old';"));
  }

  [Fact]
  public async Task V26_RoundTripsAReviewStepThroughTheStore()
  {
    SeedV25();
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    Result<Chain> created = await store.CreateAsync(
        Chain.NewFromSeeds(Ws, 5, 1, "reviewed", null,
            [ChainStepSeed.Implementing(1), new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer"))],
            DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "create must succeed");

    Result<Chain> reloaded = await store.GetAsync(created.Value.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(reloaded.IsSuccess, reloaded.Error?.Message ?? "get must succeed");
    Assert.Equal(ChainStepRoleKind.Implement, reloaded.Value.Steps[0].Role.Kind);
    Assert.Equal(ChainStepRoleKind.Review, reloaded.Value.Steps[1].Role.Kind);
    Assert.Equal("vendor/reviewer", reloaded.Value.Steps[1].Role.Model);
  }
}
