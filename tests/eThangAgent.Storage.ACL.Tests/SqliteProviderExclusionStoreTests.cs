using eThangAgent.StateDomain;

#pragma warning disable CA2007
namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteProviderExclusionStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"excl-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _db;
  private readonly SqliteProviderExclusionStore _store;

  private sealed class TestWorkspaceContext(string wsId) : IWorkspaceContext
  {
    public string WorkspaceId => wsId;
  }

  public SqliteProviderExclusionStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteProviderExclusionStore(_db, new TestWorkspaceContext("test-ws"));
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
  public async Task AddExclusion_ThenGet_ReturnsItInActiveSet()
  {
    bool added = await _store.AddExclusionAsync("model-x:ProviderY", TimeSpan.FromMinutes(10));
    Assert.True(added);

    IReadOnlySet<string> active = await _store.GetActiveExclusionsAsync();
    Assert.Contains("model-x:ProviderY", active);
  }

  [Fact]
  public async Task GetActive_AfterTtlExpiry_PurgesAndReturnsEmpty()
  {
    _ = await _store.AddExclusionAsync("model-x:ProviderY", TimeSpan.Zero);
    await Task.Delay(50);

    IReadOnlySet<string> active = await _store.GetActiveExclusionsAsync();
    Assert.Empty(active);
  }

  [Fact]
  public async Task AddExclusion_OverwritesExisting_ResetsExpiry()
  {
    _ = await _store.AddExclusionAsync("model-x:ProviderY", TimeSpan.Zero);
    await Task.Delay(50);
    _ = await _store.AddExclusionAsync("model-x:ProviderY", TimeSpan.FromMinutes(10));

    IReadOnlySet<string> active = await _store.GetActiveExclusionsAsync();
    Assert.Contains("model-x:ProviderY", active);
  }

  [Fact]
  public async Task RemoveExclusion_RemovesFromActiveSet()
  {
    _ = await _store.AddExclusionAsync("model-x:ProviderY", TimeSpan.FromMinutes(10));
    bool removed = await _store.RemoveExclusionAsync("model-x:ProviderY");
    Assert.True(removed);

    IReadOnlySet<string> active = await _store.GetActiveExclusionsAsync();
    Assert.Empty(active);
  }
}
