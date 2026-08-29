using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Bulk namespace-prefix delete over the real store: exact namespace and
/// dotted-boundary sub-namespaces are removed; siblings survive. FTS stays in sync.</summary>
public sealed class SqliteStateStorePrefixTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-prefix-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteStateStore _store;

  public SqliteStateStorePrefixTests()
  {
    _database = new AppDatabase(_dbPath);
    _store = new SqliteStateStore(_database);
  }

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
  public async Task DeletePrefix_RemovesExactAndDottedChildren_Only()
  {
    _ = await _store.SetKeyCasAsync("ws1", "sdd.alpha", "ledger", "one", null, ct: TestContext.Current.CancellationToken);
    _ = await _store.SetKeyCasAsync("ws1", "sdd.alphabeta", "ledger", "two", null, ct: TestContext.Current.CancellationToken);
    _ = await _store.SetKeyCasAsync("ws1", "sdd.other", "ledger", "three", null, ct: TestContext.Current.CancellationToken);
    _ = await _store.SetKeyCasAsync("ws2", "sdd.alpha", "ledger", "other-ws", null, ct: TestContext.Current.CancellationToken);

    int removed = await _store.DeleteNamespacePrefixAsync("ws1", "sdd.alpha", ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, removed); // dotted boundary: alphabeta survives
    Assert.Null(await _store.GetKeyAsync("ws1", "sdd.alpha", "ledger", ct: TestContext.Current.CancellationToken));
    Assert.NotNull(await _store.GetKeyAsync("ws1", "sdd.alphabeta", "ledger", ct: TestContext.Current.CancellationToken));
    Assert.NotNull(await _store.GetKeyAsync("ws1", "sdd.other", "ledger", ct: TestContext.Current.CancellationToken));
    Assert.NotNull(await _store.GetKeyAsync("ws2", "sdd.alpha", "ledger", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task DeletedKeys_LeaveTheSearchIndex()
  {
    _ = await _store.SetKeyCasAsync("ws1", "sdd.zed", "report", "xylophone content", null, ct: TestContext.Current.CancellationToken);
    Result<IReadOnlyList<StateSearchHit>> before = await _store.SearchKeysAsync("ws1", "xylophone", 20, ct: TestContext.Current.CancellationToken);
    _ = Assert.Single(before.Value!);

    _ = await _store.DeleteNamespacePrefixAsync("ws1", "sdd.zed", ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<StateSearchHit>> after = await _store.SearchKeysAsync("ws1", "xylophone", 20, ct: TestContext.Current.CancellationToken);
    Assert.True(after.IsSuccess);
    Assert.Empty(after.Value);
  }
}
