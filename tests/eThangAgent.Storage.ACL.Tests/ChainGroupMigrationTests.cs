using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>V35 adds <c>chain_steps.group_id</c> and <c>chain_steps.worktree_path</c>: the group
///     a step runs in, and the git worktree its session is anchored to.
///     <para>Pinned end-to-end - an existing V34 database upgrades in place with its rows intact,
///     every pre-existing step carries NULL for both columns (no step before this migration ran
///     in a group or a worktree, so NULL is a fact rather than a guess), and the rebuild leaves
///     no orphan table and keeps the status/role CHECKs closed.</para></summary>
public sealed class ChainGroupMigrationTests : IDisposable
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

  /// <summary>Builds a fully-migrated database, strips the two V35 columns back to the V34
  ///     shape, and stamps user_version 34 - the exact pre-V35 state a shipped database is in.
  ///     <para>The table is REBUILT here rather than altered, because SQLite cannot drop a
  ///     column: the fixture must reproduce the old shape exactly, or the migration would be
  ///     tested against a database that never existed.</para></summary>
  private void SeedV34()
  {
    _ = new AppDatabase(_dbPath);
    // V34's chain_steps carried the two model columns V37 drops, so the fixture restores that
    // shape before stamping the version: a pre-V35 database never had the present shape.
    ChainStepsV36Shape.Restore(_dbPath);
    Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v34 (
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
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v34 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model, review_attempts,
            invalidated_by)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, role_model, served_model, review_attempts, invalidated_by
                FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v34 RENAME TO chain_steps;
        PRAGMA user_version = 34;
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

  /// <summary>True when <paramref name="table"/> carries a column named
  ///     <paramref name="column"/> in the CURRENT schema.</summary>
  private bool ColumnExists(string table, string column)
      => Scalar<long>($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';") == 1L;

  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

  /// <summary>Writes one chain with two steps into the CURRENT schema, so the fixture has real
  ///     rows for the rebuild to carry forward.</summary>
  private void SeedChainRows()
  {
    AppDatabase database = new(_dbPath);
    SqliteChainStore store = new(database, Ws);
    Chain chain = Chain.NewFromSeeds(Ws, 7, 1, "Ship it", "session-root",
        [ChainStepSeed.Implementing(1), new ChainStepSeed(2, ChainStepRole.Refine)],
        T0);
    Result<Chain> created = store.CreateAsync(chain, TestContext.Current.CancellationToken)
        .GetAwaiter().GetResult();
    Assert.True(created.IsSuccess, created.Error?.Message ?? "fixture chain must be created");
  }

  /// <summary>Builds a database at the V35 shape - the current chain_steps table minus
  ///     <c>worktree_branch</c> - and stamps user_version 35, which is the exact pre-V36 state a
  ///     shipped database is in.</summary>
  private void SeedV35()
  {
    // V35's chain_steps carried the two model columns V37 drops, so the fixture restores that
    // shape before stamping the version: a pre-V36 database never had the present shape.
    ChainStepsV36Shape.Restore(_dbPath);
    Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v35b (
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
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v35b (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model, review_attempts,
            invalidated_by, group_id, worktree_path)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, role_model, served_model, review_attempts, invalidated_by,
                group_id, worktree_path FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v35b RENAME TO chain_steps;
        PRAGMA user_version = 35;
        PRAGMA foreign_keys = ON;
        """);
  }

  /// <summary>V36 stores the branch a member committed on. It is NULL for every row that
  ///     predates the column, because no step before it ran in a worktree at all.</summary>
  [Fact]
  public void V36_AddsTheWorktreeBranchColumn_AndLeavesEveryExistingRowNull()
  {
    SeedChainRows();
    SeedV35();

    _ = new AppDatabase(_dbPath);

    Assert.True(ColumnExists("chain_steps", "worktree_branch"));
    Assert.Equal(0L, Scalar<long>("SELECT COUNT(*) FROM chain_steps WHERE worktree_branch IS NOT NULL;"));
    Assert.Equal(2L, Scalar<long>("SELECT COUNT(*) FROM chain_steps;"));
  }
  [Fact]
  public void Migrate_AddsGroupAndWorktreeColumns()
  {
    _ = new AppDatabase(_dbPath);
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    connection.Open();

    Assert.True(ColumnExists("chain_steps", "group_id"));
    Assert.True(ColumnExists("chain_steps", "worktree_path"));
    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
  }

  [Fact]
  public void V35_UpgradesAV34Database_KeepingEveryRow_AndLeavingBothColumnsNull()
  {
    SeedChainRows();
    SeedV34();
    Execute("UPDATE chain_steps SET status='Done' WHERE position=1;");

    _ = new AppDatabase(_dbPath);

    long rows = Scalar<long>("SELECT COUNT(*) FROM chain_steps;");
    long grouped = Scalar<long>("SELECT COUNT(*) FROM chain_steps WHERE group_id IS NOT NULL;");
    long worktreed = Scalar<long>("SELECT COUNT(*) FROM chain_steps WHERE worktree_path IS NOT NULL;");
    string role = Scalar<string>("SELECT role FROM chain_steps WHERE position=2;");
    string status = Scalar<string>("SELECT status FROM chain_steps WHERE position=1;");

    Assert.Equal(2L, rows);
    // NO BACKFILL VALUE: no step that predates this migration ran in a group or in a worktree.
    Assert.Equal(0L, grouped);
    Assert.Equal(0L, worktreed);
    // THE ROLE IS REWRITTEN BY V42: the row said 'Review' in V34 and the vocabulary moved on.
    Assert.Equal("Refine", role);
    Assert.Equal("Done", status);
  }

  [Fact]
  public void V35_LeavesNoOrphanTable()
  {
    SeedChainRows();
    SeedV34();

    _ = new AppDatabase(_dbPath);

    long orphans = Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'chain_steps_v%';");
    Assert.Equal(0L, orphans);
  }

  [Fact]
  public void V35_KeepsTheStatusAndRoleChecksClosed()
  {
    SeedChainRows();
    SeedV34();

    _ = new AppDatabase(_dbPath);

    // A DIRECT writer - an exec script's SQL - must not be able to smuggle an out-of-enum value
    // past the domain's own validation (the V20/V21/V23/V26/V28/V31 door).
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET status='Nonsense' WHERE position=1;"));
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET role='Nonsense' WHERE position=1;"));
  }
}
