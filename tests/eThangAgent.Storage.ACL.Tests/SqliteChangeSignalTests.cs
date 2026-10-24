using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Issue #8: the workbench detects the ChildHost's writes through SQLite's
///     <c>PRAGMA data_version</c>. The contract that makes it usable is narrow and must
///     be pinned: it moves when ANOTHER connection commits, and it does NOT move for the
///     reader's own writes - otherwise every local write would trigger a redundant
///     re-read and the signal would be worthless as a cross-process trigger.</summary>
public sealed class SqliteChangeSignalTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-signal-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;

  public SqliteChangeSignalTests() => _database = new AppDatabase(_dbPath);

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

  [Fact]
  public void Read_IsStable_WhenNothingChanges()
  {
    using SqliteChangeSignal signal = new(_database);

    long first = signal.Read();
    long second = signal.Read();

    Assert.True(first > 0, "a live database reports a positive data_version");
    Assert.Equal(first, second);
  }

  [Fact]
  public void Read_Moves_WhenAnotherConnectionCommits()
  {
    using SqliteChangeSignal signal = new(_database);
    long before = signal.Read();

    // A second connection is the ChildHost: the agent's write arrives from another
    // process, and that is exactly what this signal must see.
    using (SqliteConnection other = new($"Data Source={_dbPath}"))
    {
      other.Open();
      using SqliteCommand write = other.CreateCommand();
      write.CommandText = "INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at) VALUES ('ws', 'ns', 'k', 'v', 1, 'now');";
      _ = write.ExecuteNonQuery();
    }

    Assert.NotEqual(before, signal.Read());
  }

  [Fact]
  public void Read_DoesNotMove_ForTheSignalsOwnConnection()
  {
    // The narrow fact that makes the signal meaningful: a connection's OWN commit does
    // not move its own data_version. That is why the signal must hold a dedicated
    // connection - and why every OTHER connection (the ChildHost, the app's stores)
    // does move it.
    using SqliteChangeSignal signal = new(_database);
    long before = signal.Read();

    using (SqliteConnection own = new($"Data Source={_dbPath}"))
    {
      own.Open();
      using SqliteCommand first = own.CreateCommand();
      first.CommandText = "PRAGMA data_version;";
      long ownBefore = Convert.ToInt64(first.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

      using SqliteCommand write = own.CreateCommand();
      write.CommandText = "INSERT INTO state_keys (workspace_id, ns, name, value, version, updated_at) VALUES ('ws', 'ns', 'own', 'v', 1, 'now');";
      _ = write.ExecuteNonQuery();

      using SqliteCommand after = own.CreateCommand();
      after.CommandText = "PRAGMA data_version;";
      long ownAfter = Convert.ToInt64(after.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

      // Its own write left its own counter alone...
      Assert.Equal(ownBefore, ownAfter);
    }

    // ...while the signal's connection, which is a DIFFERENT connection, saw it.
    Assert.NotEqual(before, signal.Read());
  }
}
