using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Integration: FTS search over state_keys through the real store,
/// workspace-scoped, with malformed queries surfaced as InvalidQuery failures.</summary>
public class SqliteStateStoreSearchTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-search-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteStateStore _store;

  public SqliteStateStoreSearchTests()
  {
    _database = new AppDatabase(_dbPath);
    _store = new SqliteStateStore(_database);
  }

  public void Dispose()
  {
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
  }

  [Fact]
  public async Task Search_FindsByValueTerm()
  {
    _ = await _store.SetKeyCasAsync("ws1", "plans", "alpha", "rewrite the SDD ledger flow", null);
    Result<IReadOnlyList<StateSearchHit>> r = await _store.SearchKeysAsync("ws1", "ledger", 20);
    Assert.True(r.IsSuccess);
    StateSearchHit hit = Assert.Single(r.Value!);
    Assert.Equal("plans", hit.Ns);
    Assert.Equal("alpha", hit.Name);
    Assert.Contains("ledger", hit.Snippet);
  }

  [Fact]
  public async Task Search_FindsByNamespaceOrNameTerm()
  {
    _ = await _store.SetKeyCasAsync("ws1", "specs", "2026-08-24-native-skills-db-planning", "body text here", null);
    Result<IReadOnlyList<StateSearchHit>> r = await _store.SearchKeysAsync("ws1", "native* AND skills*", 20);
    Assert.True(r.IsSuccess);
    _ = Assert.Single(r.Value!);
  }

  [Fact]
  public async Task Search_IsWorkspaceScoped()
  {
    _ = await _store.SetKeyCasAsync("ws1", "notes", "mine", "xylophone collection", null);
    _ = await _store.SetKeyCasAsync("ws2", "notes", "theirs", "xylophone collection", null);
    Result<IReadOnlyList<StateSearchHit>> r = await _store.SearchKeysAsync("ws1", "xylophone", 20);
    Assert.True(r.IsSuccess);
    StateSearchHit hit = Assert.Single(r.Value!);
    Assert.Equal("mine", hit.Name);
  }

  [Fact]
  public async Task Search_MalformedQuery_YieldsInvalidQuery_NotThrow()
  {
    _ = await _store.SetKeyCasAsync("ws1", "notes", "a", "content", null);
    Result<IReadOnlyList<StateSearchHit>> r = await _store.SearchKeysAsync("ws1", "AND (", 20);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidQuery", r.Error!.Code);
  }

  [Fact]
  public async Task Search_LimitRespected()
  {
    for (int i = 0; i < 5; i++)
    {
      _ = await _store.SetKeyCasAsync("ws1", "notes", $"k{i}", "shared term everywhere", null);
    }

    Result<IReadOnlyList<StateSearchHit>> r = await _store.SearchKeysAsync("ws1", "shared", 3);
    Assert.True(r.IsSuccess);
    Assert.Equal(3, r.Value!.Count);
  }
}
