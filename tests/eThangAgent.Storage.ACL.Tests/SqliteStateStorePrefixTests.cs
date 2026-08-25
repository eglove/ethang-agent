using eThangAgent.Storage.ACL;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Bulk namespace-prefix delete over the real store: exact namespace and
/// dotted-boundary sub-namespaces are removed; siblings survive. FTS stays in sync.</summary>
public class SqliteStateStorePrefixTests : IDisposable
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
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task DeletePrefix_RemovesExactAndDottedChildren_Only()
    {
        await _store.SetKeyCasAsync("ws1", "sdd.alpha", "ledger", "one", null);
        await _store.SetKeyCasAsync("ws1", "sdd.alphabeta", "ledger", "two", null);
        await _store.SetKeyCasAsync("ws1", "sdd.other", "ledger", "three", null);
        await _store.SetKeyCasAsync("ws2", "sdd.alpha", "ledger", "other-ws", null);

        var removed = await _store.DeleteNamespacePrefixAsync("ws1", "sdd.alpha");

        Assert.Equal(1, removed); // dotted boundary: alphabeta survives
        Assert.Null(await _store.GetKeyAsync("ws1", "sdd.alpha", "ledger"));
        Assert.NotNull(await _store.GetKeyAsync("ws1", "sdd.alphabeta", "ledger"));
        Assert.NotNull(await _store.GetKeyAsync("ws1", "sdd.other", "ledger"));
        Assert.NotNull(await _store.GetKeyAsync("ws2", "sdd.alpha", "ledger"));
    }

    [Fact]
    public async Task DeletedKeys_LeaveTheSearchIndex()
    {
        await _store.SetKeyCasAsync("ws1", "sdd.zed", "report", "xylophone content", null);
        var before = await _store.SearchKeysAsync("ws1", "xylophone", 20);
        Assert.Single(before.Value!);

        await _store.DeleteNamespacePrefixAsync("ws1", "sdd.zed");

        var after = await _store.SearchKeysAsync("ws1", "xylophone", 20);
        Assert.True(after.IsSuccess);
        Assert.Empty(after.Value!);
    }
}
