#pragma warning disable IDE0005
using Microsoft.Data.Sqlite;
#pragma warning restore IDE0005

#pragma warning disable CA2007
namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteAppPreferenceStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"prefs-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteAppPreferenceStore _store;

  public SqliteAppPreferenceStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteAppPreferenceStore(_db);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
#pragma warning disable CA1031
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031
  }

  [Fact]
  public async Task GetAsync_UnsetKey_ReturnsNull()
      => Assert.Null(await _store.GetAsync("active_provider"));

  [Fact]
  public async Task SetAsync_ThenGetAsync_RoundTrips()
  {
    Assert.True(await _store.SetAsync("active_provider", "zai"));
    Assert.Equal("zai", await _store.GetAsync("active_provider"));
  }

  [Fact]
  public async Task SetAsync_Twice_Overwrites()
  {
    _ = await _store.SetAsync("active_provider", "zai");
    Assert.True(await _store.SetAsync("active_provider", "openrouter"));
    Assert.Equal("openrouter", await _store.GetAsync("active_provider"));
  }

  [Fact]
  public async Task GetSet_WithBlankKeyOrValue_IsRejected()
  {
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync(" "));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("", "v"));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("k", " "));
  }

  [Fact]
  public void MigrationV7_CreatesAppPreferencesTable()
  {
    // Fresh databases migrate to version 7 with the app_preferences table present.
    using SqliteConnection connection = _db.Open();
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = "PRAGMA user_version;";
    Assert.Equal(7, Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));

    using SqliteCommand table = connection.CreateCommand();
    table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_preferences';";
    Assert.Equal(1L, Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
  }
}
