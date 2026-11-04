using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

// Issue #356: advisory diagnostics are never persisted. The session-local ledger
// is in-memory only; the app database schema carries no advisory table.
public sealed class AdvisoryDiagnosticsPersistenceTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-adv-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;

  public AdvisoryDiagnosticsPersistenceTests() => _database = new AppDatabase(_dbPath);

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    try
    {
      File.Delete(_dbPath);
    }
    catch (IOException)
    {
      Assert.True(true);
    }
    catch (UnauthorizedAccessException)
    {
      Assert.True(true);
    }
  }

  [Fact]
  public void NoAdvisoryTable_InSchema()
  {
    using SqliteConnection connection = _database.OpenReadOnly();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE '%advisory%';";
    Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
  }

  [Fact]
  public void NoDiagnosticsTable_InSchema()
  {
    using SqliteConnection connection = _database.OpenReadOnly();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE '%diagnostic%';";
    Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
  }
}
