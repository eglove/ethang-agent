using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Migration V22 (issue #298): the smart-selection feature is deleted, so the
///     <c>provider_exclusions</c> table it wrote goes with it. A V21 database carrying the
///     table upgrades in place, loses exactly that table, and keeps every other table and
///     row; re-opening is a no-op. V6 still CREATES the table on a fresh database so both
///     paths walk the same history - the V4/V18 curated-memories precedent.</summary>
public sealed class ProviderExclusionRemovalMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-v22mig-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  /// <summary>Builds a fully-migrated database carrying real rows, recreates the V6
  ///     provider_exclusions shape V22 removes, seeds a row into it (so the drop is proven
  ///     to take DATA as well as structure), and stamps user_version 21 - the exact
  ///     pre-V22 state.</summary>
  private void SeedV21Shape()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the SQL is a literal fixture, not user input.
#pragma warning disable CA2100
    command.CommandText = string.Join('\n',
        "CREATE TABLE provider_exclusions (",
        "    model_provider_key TEXT NOT NULL,",
        "    workspace_id       TEXT NOT NULL,",
        "    expires_at         TEXT NOT NULL,",
        "    created_at         TEXT NOT NULL,",
        "    PRIMARY KEY (model_provider_key, workspace_id)",
        ");",
        "INSERT INTO provider_exclusions (model_provider_key, workspace_id, expires_at, created_at)",
        "VALUES ('deepseek/deepseek-v4.1-flash-1017|Reka', 'ws-demo', 't', 't');",
        "PRAGMA user_version = 21;");
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }

  [Fact]
  public void V22_Drops_Provider_Exclusions_And_Leaves_Other_Tables_Untouched()
  {
    SeedV21Shape();

    _ = new AppDatabase(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(22, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='provider_exclusions';"));
    // Every earlier table and row is untouched.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='app_preferences';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='state_keys';"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agents';"));
  }

  [Fact]
  public void V22_Is_Idempotent_Reopening_Is_A_NoOp()
  {
    SeedV21Shape();

    _ = new AppDatabase(_dbPath); // migrate once
    _ = new AppDatabase(_dbPath); // reopen: no-op

    using SqliteConnection connection = Open();
    Assert.Equal(22, Version(connection));
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='provider_exclusions';"));
  }

  [Fact]
  public void Fresh_Database_Ends_Without_Provider_Exclusions()
  {
    _ = new AppDatabase(_dbPath);

    using SqliteConnection connection = Open();
    Assert.Equal(22, Version(connection));
    // V6 creates it and V22 drops it: a fresh database walks the same path an existing
    // one did and ends in the same place.
    Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='provider_exclusions';"));
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    return connection;
  }

  private static int Version(SqliteConnection connection)
  {
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private static long Scalar(SqliteConnection connection, string sql)
  {
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): callers pass literal fixture SQL, never user input.
#pragma warning disable CA2100
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
