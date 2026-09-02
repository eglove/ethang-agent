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
#pragma warning disable CA1031, S108
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task GetAsync_UnsetKey_ReturnsNull()
      => Assert.Null(await _store.GetAsync("active_provider", ct: TestContext.Current.CancellationToken));

  [Fact]
  public async Task SetAsync_ThenGetAsync_RoundTrips()
  {
    Assert.True(await _store.SetAsync("active_provider", "zai", ct: TestContext.Current.CancellationToken));
    Assert.Equal("zai", await _store.GetAsync("active_provider", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task SetAsync_Twice_Overwrites()
  {
    _ = await _store.SetAsync("active_provider", "zai", ct: TestContext.Current.CancellationToken);
    Assert.True(await _store.SetAsync("active_provider", "openrouter", ct: TestContext.Current.CancellationToken));
    Assert.Equal("openrouter", await _store.GetAsync("active_provider", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task GetSet_WithBlankKeyOrValue_IsRejected()
  {
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync(" ", ct: TestContext.Current.CancellationToken));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("", "v", ct: TestContext.Current.CancellationToken));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("k", " ", ct: TestContext.Current.CancellationToken));
    _ = await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(" ", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DeleteAsync_Removes_The_Preference_And_Reports_It()
  {
    _ = await _store.SetAsync("active_provider", "zai", ct: TestContext.Current.CancellationToken);

    Assert.True(await _store.DeleteAsync("active_provider", ct: TestContext.Current.CancellationToken));
    Assert.Null(await _store.GetAsync("active_provider", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DeleteAsync_AbsentKey_ReturnsFalse()
      => Assert.False(await _store.DeleteAsync("never-set", ct: TestContext.Current.CancellationToken));

  [Fact]
  public async Task DeleteAsync_ThenSetAsync_Restores_The_Preference()
  {
    _ = await _store.SetAsync("active_provider", "zai", ct: TestContext.Current.CancellationToken);
    _ = await _store.DeleteAsync("active_provider", ct: TestContext.Current.CancellationToken);

    Assert.True(await _store.SetAsync("active_provider", "openrouter", ct: TestContext.Current.CancellationToken));
    Assert.Equal("openrouter", await _store.GetAsync("active_provider", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public void MigrationV7_CreatesAppPreferencesTable()
  {
    // Fresh databases migrate past the app_preferences and session-binding migrations.
    using SqliteConnection connection = _db.Open();
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = "PRAGMA user_version;";
    Assert.Equal(12, Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));

    using SqliteCommand table = connection.CreateCommand();
    table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_preferences';";
    Assert.Equal(1L, Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
  }
}
