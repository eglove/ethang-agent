using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #320: V29 adds <c>chain_steps.review_attempts</c>, the review round-trip
///     counter Q14 keeps separate from the retry counter. Pinned end-to-end - an existing V28
///     database upgrades in place with its rows intact, every pre-existing step reads 0 (nobody
///     was ever sent back before this column existed), and the rebuild leaves no orphan table
///     and keeps the status/role CHECKs closed.</summary>
public sealed class ChainReviewAttemptsMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-chainv29-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, strips <c>review_attempts</c> back to the V28
  ///     shape, and stamps user_version 28 - the exact pre-V29 state a shipped database is in.
  ///     <para>THE PRE-V29 VERSION IS A LITERAL, deliberately: 'latest minus one' would silently
  ///     change meaning the moment a V30 exists, and this fixture must reproduce V28 forever.</para>
  ///     </summary>
  private void SeedV28() =>
      Execute("""
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v28 (
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
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v28 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, role_model, served_model FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v28 RENAME TO chain_steps;
        PRAGMA user_version = 28;
        PRAGMA foreign_keys = ON;
        """);

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
  public void V29_AddsReviewAttempts_AndStampsTheLatestVersion()
  {
    _ = new AppDatabase(_dbPath);

    long version = Scalar<long>("PRAGMA user_version;");
    long columns = Scalar<long>(
        "SELECT COUNT(*) FROM pragma_table_info('chain_steps') WHERE name='review_attempts';");

    Assert.Equal(AppDatabase.LatestSchemaVersion, version);
    Assert.Equal(1L, columns);
  }

  [Fact]
  public void V29_UpgradesAV28Database_KeepingEveryRow_AndZeroingReviewAttempts()
  {
    SeedChainRows();
    SeedV28();
    Execute("UPDATE chain_steps SET status='Done', attempts=2 WHERE position=1;");

    _ = new AppDatabase(_dbPath);

    long rows = Scalar<long>("SELECT COUNT(*) FROM chain_steps;");
    long sentBack = Scalar<long>("SELECT COUNT(*) FROM chain_steps WHERE review_attempts <> 0;");
    string role = Scalar<string>("SELECT role FROM chain_steps WHERE position=2;");
    long attempts = Scalar<long>("SELECT attempts FROM chain_steps WHERE position=1;");

    Assert.Equal(2L, rows);
    // NO BACKFILL VALUE NEEDED: every step that settled before this migration was never sent
    // back, which is exactly what 0 says - unlike served_model (V28), where a guess would
    // fabricate a comparison. The RETRY counter is carried forward untouched.
    Assert.Equal(0L, sentBack);
    Assert.Equal("Review", role);
    Assert.Equal(2L, attempts);
  }

  [Fact]
  public void V29_LeavesNoOrphanTable()
  {
    SeedChainRows();
    SeedV28();

    _ = new AppDatabase(_dbPath);

    long orphans = Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'chain_steps_v%';");
    Assert.Equal(0L, orphans);
  }

  [Fact]
  public void V29_KeepsTheStatusAndRoleChecksClosed_AndRefusesANegativeCounter()
  {
    SeedChainRows();
    SeedV28();

    _ = new AppDatabase(_dbPath);

    // A DIRECT writer - an exec script's SQL - must not smuggle an out-of-enum value past the
    // domain's validation (the V20/V21/V23/V26/V28 door), nor make the cap unreachable with a
    // negative round-trip count.
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET status='Nonsense' WHERE position=1;"));
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET role='Nonsense' WHERE position=1;"));
    _ = Assert.Throws<SqliteException>(() =>
        Execute("UPDATE chain_steps SET review_attempts=-1 WHERE position=1;"));
  }

  [Fact]
  public async Task ReviewAttempts_RoundTrip_ThroughTheStore()
  {
    SeedChainRows();
    AppDatabase database = new(_dbPath);
    SqliteChainStore store = new(database, Ws);
    Chain created = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));

    // Drive the step through one full review round trip: claim, settle, reopen, claim, settle.
    Chain reopened = created.ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .ReopenStep(1, ChainActor.Harness, "the reviewer said no", T0);
    Result<Chain> saved = await store.SaveAsync(reopened, created.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(saved.IsSuccess, saved.Error?.Message ?? "reopen must save");

    Chain reloaded = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(1, reloaded.StepAt(1)!.ReviewAttempts);
    Assert.Equal(1, reloaded.StepAt(1)!.Attempts);
    Assert.Equal(0, reloaded.StepAt(2)!.ReviewAttempts);
  }
}
