using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #218: V28 adds <c>chain_steps.served_model</c>, the model a step's session
///     actually served on. Pinned end-to-end - an existing V27 database upgrades in place with
///     its rows intact, every pre-existing step carries NULL (nobody recorded a model before this
///     column existed, and a guess would fabricate a comparison the separation rule would act on),
///     and the rebuild leaves no orphan table and keeps the status/role CHECKs closed.</summary>
public sealed class ChainServedModelMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-chainv28-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, strips <c>served_model</c> back to the V27
  ///     shape, and stamps user_version 27 - the exact pre-V28 state a shipped database is in.
  ///     <para>The table is REBUILT here rather than altered, because SQLite cannot drop a
  ///     column: the fixture must reproduce the old shape exactly, or the migration would be
  ///     tested against a database that never existed.</para></summary>
  private void SeedV27()
  {
    _ = new AppDatabase(_dbPath);
    Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v27 (
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
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v27 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, role_model FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v27 RENAME TO chain_steps;
        PRAGMA user_version = 27;
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

  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Writes one chain with two steps into the CURRENT schema, so the fixture has real
  ///     rows for the rebuild to carry forward.</summary>
  private void SeedChainRows()
  {
    AppDatabase database = new(_dbPath);
    SqliteChainStore store = new(database, Ws);
    Chain chain = Chain.NewFromSeeds(Ws, 7, 1, "Ship it", "session-root",
        [ChainStepSeed.Implementing(1), new ChainStepSeed(2, ChainStepRole.Review("vendor/reviewer"))],
        T0);
    Result<Chain> created = store.CreateAsync(chain, TestContext.Current.CancellationToken)
        .GetAwaiter().GetResult();
    Assert.True(created.IsSuccess, created.Error?.Message ?? "fixture chain must be created");
  }

  [Fact]
  public void V28_AddsServedModel_AndStampsTheLatestVersion()
  {
    _ = new AppDatabase(_dbPath);

    long version = Scalar<long>("PRAGMA user_version;");
    long columns = Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('chain_steps') WHERE name='served_model';");

    Assert.Equal(AppDatabase.LatestSchemaVersion, version);
    Assert.Equal(1L, columns);
  }

  [Fact]
  public void V28_UpgradesAV27Database_KeepingEveryRow_AndLeavingServedModelNull()
  {
    SeedChainRows();
    SeedV27();
    // A step that DID record a role survives the V28 rebuild unchanged.
    Execute("UPDATE chain_steps SET status='Done' WHERE position=1;");

    _ = new AppDatabase(_dbPath);

    long rows = Scalar<long>("SELECT COUNT(*) FROM chain_steps;");
    long recorded = Scalar<long>("SELECT COUNT(*) FROM chain_steps WHERE served_model IS NOT NULL;");
    string role = Scalar<string>("SELECT role FROM chain_steps WHERE position=2;");
    string status = Scalar<string>("SELECT status FROM chain_steps WHERE position=1;");

    Assert.Equal(2L, rows);
    // NO BACKFILL VALUE: every pre-existing step ran on a model nobody recorded, and writing a
    // guess would fabricate a comparison the separation rule would then act on.
    Assert.Equal(0L, recorded);
    Assert.Equal("Review", role);
    Assert.Equal("Done", status);
  }

  [Fact]
  public void V28_LeavesNoOrphanTable()
  {
    SeedChainRows();
    SeedV27();

    _ = new AppDatabase(_dbPath);

    long orphans = Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'chain_steps_v%';");
    Assert.Equal(0L, orphans);
  }

  [Fact]
  public void V28_KeepsTheStatusAndRoleChecksClosed()
  {
    SeedChainRows();
    SeedV27();

    _ = new AppDatabase(_dbPath);

    // A DIRECT writer - an exec script's SQL - must not be able to smuggle an out-of-enum value
    // past the domain's own validation (the V20/V21/V23/V26 door).
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET status='Nonsense' WHERE position=1;"));
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET role='Nonsense' WHERE position=1;"));
  }

  [Fact]
  public async Task ServedModel_RoundTrips_ThroughTheStore()
  {
    SeedChainRows();
    AppDatabase database = new(_dbPath);
    SqliteChainStore store = new(database, Ws);
    Chain created = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));

    Result<Chain> stamped = await store.SaveAsync(
        created.RecordServedModel(1, "vendor/implementer", T0), created.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(stamped.IsSuccess, stamped.Error?.Message ?? "stamp must save");

    Chain reloaded = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal("vendor/implementer", reloaded.StepAt(1)!.ServedModel);
    // The REVIEW step recorded no model, and that is a different fact from an empty one.
    Assert.Null(reloaded.StepAt(2)!.ServedModel);
  }
}
