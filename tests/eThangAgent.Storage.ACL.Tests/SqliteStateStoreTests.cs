using System.Globalization;
using eThangAgent.StateDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteStateStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-state-{Guid.NewGuid():N}.db");
  private readonly SqliteStateStore _store;

  public SqliteStateStoreTests()
      => _store = new SqliteStateStore(new AppDatabase(_dbPath));

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
  public async Task Set_InsertThenUpsert_BumpsVersions()
  {
    StateKeyValue? first = await _store.SetKeyCasAsync("ws", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);
    StateKeyValue? second = await _store.SetKeyCasAsync("ws", "current", "head", "b", null, ct: TestContext.Current.CancellationToken);

    Assert.Equal(1, first!.Version);
    Assert.Equal(2, second!.Version);
    Assert.Equal("b", second.Value);
  }

  [Fact]
  public async Task Cas_StaleVersion_ReturnsNull_AndKeepsRow()
  {
    _ = await _store.SetKeyCasAsync("ws", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);

    StateKeyValue? conflict = await _store.SetKeyCasAsync("ws", "current", "head", "b", 5, ct: TestContext.Current.CancellationToken);

    Assert.Null(conflict);
    StateKeyValue? row = await _store.GetKeyAsync("ws", "current", "head", ct: TestContext.Current.CancellationToken);
    Assert.Equal("a", row!.Value);
    Assert.Equal(1, row.Version);
  }

  [Fact]
  public async Task Cas_MatchingVersion_Succeeds()
  {
    _ = await _store.SetKeyCasAsync("ws", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);

    StateKeyValue? saved = await _store.SetKeyCasAsync("ws", "current", "head", "b", 1, ct: TestContext.Current.CancellationToken);

    Assert.NotNull(saved);
    Assert.Equal(2, saved.Version);
  }

  [Fact]
  public async Task Delete_RespectsExpectedVersion_AndMissingKeys()
  {
    _ = await _store.SetKeyCasAsync("ws", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);

    Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", 5, ct: TestContext.Current.CancellationToken));
    Assert.True(await _store.DeleteKeyCasAsync("ws", "current", "head", 1, ct: TestContext.Current.CancellationToken));
    Assert.False(await _store.DeleteKeyCasAsync("ws", "current", "head", null, ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task List_FiltersByNamespace()
  {
    _ = await _store.SetKeyCasAsync("ws", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);
    _ = await _store.SetKeyCasAsync("ws", "goal", "check", "[]", null, ct: TestContext.Current.CancellationToken);

    IReadOnlyList<StateKeyValue> all = await _store.ListKeysAsync("ws", null, ct: TestContext.Current.CancellationToken);
    IReadOnlyList<StateKeyValue> currentOnly = await _store.ListKeysAsync("ws", "current", ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, all.Count);
    _ = Assert.Single(currentOnly);
  }

  [Fact]
  public async Task Workspaces_AreIsolated()
  {
    _ = await _store.SetKeyCasAsync("ws-a", "current", "head", "a", null, ct: TestContext.Current.CancellationToken);

    Assert.Null(await _store.GetKeyAsync("ws-b", "current", "head", ct: TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task Transitions_PendingSelection_StatusUpdates()
  {
    TransitionRecord record = new("tr-1", "coding", "done", "work",
        ["Write-Output ok"], "pending", DateTimeOffset.UtcNow);
    _ = await _store.InsertTransitionAsync("ws", record, ct: TestContext.Current.CancellationToken);

    IReadOnlyList<TransitionRecord> pending = await _store.GetTransitionsAsync("ws", [], ct: TestContext.Current.CancellationToken);
    IReadOnlyList<TransitionRecord> byId = await _store.GetTransitionsAsync("ws", ["tr-1"], ct: TestContext.Current.CancellationToken);

    _ = Assert.Single(pending);
    _ = Assert.Single(byId);
    Assert.Equal(["Write-Output ok"], byId[0].Evidence);

    await _store.SetTransitionStatusAsync("ws", "tr-1", "certified", ct: TestContext.Current.CancellationToken);

    Assert.Empty(await _store.GetTransitionsAsync("ws", [], ct: TestContext.Current.CancellationToken));
    Assert.Equal("certified", (await _store.GetTransitionsAsync("ws", ["tr-1"], ct: TestContext.Current.CancellationToken))[0].Status);
  }

  [Fact]
  public async Task Events_Append_NewestFirst_LimitRespected()
  {
    await _store.AppendEventAsync("ws", "a", "{}", ct: TestContext.Current.CancellationToken);
    await _store.AppendEventAsync("ws", "b", "{}", ct: TestContext.Current.CancellationToken);
    await _store.AppendEventAsync("ws", "c", "{}", ct: TestContext.Current.CancellationToken);

    IReadOnlyList<StateEvent> events = await _store.GetEventsAsync("ws", 2, ct: TestContext.Current.CancellationToken);

    Assert.Equal(2, events.Count);
    Assert.Equal("c", events[0].Kind);
    Assert.Equal("b", events[1].Kind);
  }

  [Fact]
  public void Migrations_AreIdempotent()
  {
    AppDatabase second = new(_dbPath);
    using SqliteConnection connection = second.Open();

    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('state_keys','transitions','state_events');";
    Assert.Equal(3L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
  }
}
