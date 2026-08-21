using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Storage.ACL.Tests;

public class SqliteStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");
    private readonly SqliteStateStore _store;

    public SqliteStateStoreTests()
        => _store = new SqliteStateStore(new AppDatabase(_dbPath));

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Set_InsertThenUpsert_BumpsVersions()
    {
        var first = await _store.SetKeyCasAsync("ws", "current", "head", "a", null);
        var second = await _store.SetKeyCasAsync("ws", "current", "head", "b", null);

        Assert.Equal(1, first!.Version);
        Assert.Equal(2, second!.Version);
        Assert.Equal("b", second.Value);
    }

    [Fact]
    public async Task Cas_StaleVersion_ReturnsNull_AndKeepsRow()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        var conflict = await _store.SetKeyCasAsync("ws", "current", "head", "b", 5);

        Assert.Null(conflict);
        var row = await _store.GetKeyAsync("ws", "current", "head");
        Assert.Equal("a", row!.Value);
        Assert.Equal(1, row.Version);
    }

    [Fact]
    public async Task Cas_MatchingVersion_Succeeds()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        var saved = await _store.SetKeyCasAsync("ws", "current", "head", "b", 1);

        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Version);
    }

    [Fact]
    public async Task Delete_RespectsExpectedVersion_AndMissingKeys()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);

        Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", 5));
        Assert.True(await _store.DeleteKeyCasAsync("ws", "current", "head", 1));
        Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", null));
    }

    [Fact]
    public async Task List_FiltersByNamespace()
    {
        await _store.SetKeyCasAsync("ws", "current", "head", "a", null);
        await _store.SetKeyCasAsync("ws", "goal", "check", "[]", null);

        var all = await _store.ListKeysAsync("ws", null);
        var currentOnly = await _store.ListKeysAsync("ws", "current");

        Assert.Equal(2, all.Count);
        Assert.Single(currentOnly);
    }

    [Fact]
    public async Task Workspaces_AreIsolated()
    {
        await _store.SetKeyCasAsync("ws-a", "current", "head", "a", null);

        Assert.Null(await _store.GetKeyAsync("ws-b", "current", "head"));
    }

    [Fact]
    public async Task Transitions_PendingSelection_StatusUpdates()
    {
        var record = new TransitionRecord("tr-1", "coding", "done", "work",
            ["Write-Output ok"], "pending", DateTimeOffset.UtcNow);
        await _store.InsertTransitionAsync("ws", record);

        var pending = await _store.GetTransitionsAsync("ws", []);
        var byId = await _store.GetTransitionsAsync("ws", ["tr-1"]);

        Assert.Single(pending);
        Assert.Single(byId);
        Assert.Equal(["Write-Output ok"], byId[0].Evidence);

        await _store.SetTransitionStatusAsync("ws", "tr-1", "certified");

        Assert.Empty(await _store.GetTransitionsAsync("ws", []));
        Assert.Equal("certified", (await _store.GetTransitionsAsync("ws", ["tr-1"]))[0].Status);
    }

    [Fact]
    public async Task Events_Append_NewestFirst_LimitRespected()
    {
        await _store.AppendEventAsync("ws", "a", "{}");
        await _store.AppendEventAsync("ws", "b", "{}");
        await _store.AppendEventAsync("ws", "c", "{}");

        var events = await _store.GetEventsAsync("ws", 2);

        Assert.Equal(2, events.Count);
        Assert.Equal("c", events[0].Kind);
        Assert.Equal("b", events[1].Kind);
    }

    [Fact]
    public void Migrations_AreIdempotent()
    {
        var second = new AppDatabase(_dbPath);
        using var connection = second.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('state_keys','transitions','state_events');";
        Assert.Equal(3L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
