using System.Globalization;
using eThangAgent.AgentDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The agent_links migration (V12, W2.1/W2.5): a V11 database upgrades in place,
///     every earlier table and row is untouched, and concurrent opens serialize through the
///     process-wide migration gate into one applied schema.</summary>
public sealed class LinkMigrationTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ethang-linksmig-{Guid.NewGuid():N}.db");

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

  /// <summary>Builds a fully-migrated database carrying real rows, then rolls it back to
  ///     its exact V11 shape: V12's only effect (the agent_links table) dropped and
  ///     user_version stamped 11.</summary>
  private async Task SeedV11ShapeAsync()
  {
    AppDatabase db = new(_dbPath);
    AgentRecord root = AgentRecord.Root(AgentId.NewId(), DateTimeOffset.UtcNow, @"C:\workspaces\demo", "openrouter");
    _ = await new SqliteAgentStore(db).SaveAsync(root).ConfigureAwait(false);
    SqliteStateStore state = new(db);
    _ = await state.SetKeyCasAsync("ws", "ns", "seed-key", "v", null).ConfigureAwait(false);

    using SqliteConnection connection = Open();
    using SqliteCommand drop = connection.CreateCommand();
    drop.CommandText = "DROP TABLE IF EXISTS agent_links;";
    _ = await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = "PRAGMA user_version = 11;";
    _ = await version.ExecuteNonQueryAsync().ConfigureAwait(false);
  }

  [Fact]
  public async Task V12_Applies_Over_V11_Leaving_Earlier_Rows_Untouched_And_Store_Works()
  {
    await SeedV11ShapeAsync().ConfigureAwait(true);

    AppDatabase migrated = new(_dbPath); // constructor migrates

    using SqliteConnection connection = Open();
    Assert.Equal(12, Version(connection));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_links';"));
    // Every earlier table and row is untouched.
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM agents;"));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM state_keys;"));

    // The store round-trips over the upgraded database.
    SqliteLinkStore store = new(migrated);
    _ = store.Upsert("ws", new StoredLink("post", "c", "00000000-0000-0000-0000-000000000009", DateTimeOffset.UtcNow));
    _ = Assert.Single(store.List("ws").Value!);
  }

  [Fact]
  public async Task V12_Is_Idempotent_Under_Concurrent_Open()
  {
    await SeedV11ShapeAsync().ConfigureAwait(true);

    Task<AppDatabase>[] both =
    [
        Task.Run(() => new AppDatabase(_dbPath)),
        Task.Run(() => new AppDatabase(_dbPath)),
    ];
    _ = await Task.WhenAll(both).ConfigureAwait(true);

    using SqliteConnection connection = Open();
    Assert.Equal(12, Version(connection));
    Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='agent_links';"));
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
#pragma warning disable CA2100 // constant test-only SQL
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
