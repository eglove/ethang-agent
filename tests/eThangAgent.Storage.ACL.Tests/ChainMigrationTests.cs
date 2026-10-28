using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #300: V23 adds chains + chain_steps. The migration is pinned end-to-end
///     — an existing V22 database upgrades in place with its rows intact, both status
///     columns are CHECK-constrained closed sets (the V20/V21 precedent, so a DIRECT
///     writer cannot smuggle an out-of-enum value), and the step table carries the claim
///     clock the TTL reads.</summary>
public sealed class ChainMigrationTests : IDisposable
{
  private const string Ws = "C:\\ws\\alpha";
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-chainv23-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database, drops the V23 tables and stamps
  ///     user_version 22 — the exact pre-V23 state a shipped database is in.</summary>
  private void SeedV22()
  {
    _ = new AppDatabase(_dbPath);
    Execute("DROP TABLE IF EXISTS chain_steps; DROP TABLE IF EXISTS chains; PRAGMA user_version = 22;");
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
  public void V23_CreatesBothTables_AndStampsVersion23()
  {
    SeedV22();
    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='chains';"));
    Assert.Equal(1L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='chain_steps';"));
  }

  [Fact]
  public async Task V23_IsIdempotent_ReopeningKeepsTheTablesAndTheRows()
  {
    SeedV22();
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    Result<Chain> created = await store.CreateAsync(
        Chain.New(Ws, 5, 1, "kept", null, [1], DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "create must succeed");
    int chainId = created.Value.Id;

    _ = new AppDatabase(_dbPath); // reopen: no-op

    Assert.Equal(1L, Scalar<long>($"SELECT COUNT(*) FROM chains WHERE id = {chainId};"));
    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar<long>("PRAGMA user_version;"));
  }

  [Fact]
  public void V23_BothStatusColumnsCarryCheckConstraints()
  {
    SeedV22();
    _ = new AppDatabase(_dbPath);

    string chainsSql = Scalar<string>(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='chains';");
    string stepsSql = Scalar<string>(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='chain_steps';");

    Assert.Contains("CHECK", chainsSql, StringComparison.Ordinal);
    Assert.Contains("CHECK", stepsSql, StringComparison.Ordinal);
    Assert.Contains("'Parked'", chainsSql, StringComparison.Ordinal);
    Assert.Contains("'Skipped'", stepsSql, StringComparison.Ordinal);
  }

  /// <summary>The claim clock is a COLUMN, not a derived value: the TTL is computed on read
  ///     from it, so a crashed process cannot leave a claim alive.</summary>
  [Fact]
  public async Task V23_ChainStepsCarryTheClaimClockAndTheActor()
  {
    SeedV22();
    _ = new AppDatabase(_dbPath);

    List<string> columns = [];
    using SqliteConnection connection = new($"Data Source={_dbPath}");
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT name FROM pragma_table_info('chain_steps');";
    using SqliteDataReader reader = await command.ExecuteReaderAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
    {
      columns.Add(reader.GetString(0));
    }

    Assert.Contains("claimed_at", columns, StringComparer.Ordinal);
    Assert.Contains("actor", columns, StringComparer.Ordinal);
    Assert.Contains("attempts", columns, StringComparer.Ordinal);
    Assert.Contains("session_id", columns, StringComparer.Ordinal);
    Assert.Contains("outcome", columns, StringComparer.Ordinal);
  }

  [Fact]
  public void V23_LeavesNoOrphanTables()
  {
    SeedV22();
    _ = new AppDatabase(_dbPath);
    Assert.Equal(0L, Scalar<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%_v23' OR name LIKE '%_old';"));
  }
}
