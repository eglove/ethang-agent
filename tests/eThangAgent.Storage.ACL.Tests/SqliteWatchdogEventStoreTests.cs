#pragma warning disable IDE0005
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
#pragma warning restore IDE0005

#pragma warning disable CA2007
namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Round-trips the watchdog event audit trail: append, newest-first listing with
///     limit, per-agent kind counting, and the V9 migration's table creation.</summary>
public sealed class SqliteWatchdogEventStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wdog-" + Guid.NewGuid().ToString("N") + ".db");
  private readonly AppDatabase _db;
  private readonly SqliteWatchdogEventStore _store;

  public SqliteWatchdogEventStoreTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteWatchdogEventStore(_db);
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
#pragma warning disable CA1031, S108
    try
    {
      File.Delete(_dbPath);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task Append_ListRecent_Count_RoundTrip()
  {
    AgentId id = new(Guid.NewGuid());
    WatchdogEvent first = new(Guid.NewGuid(), id, WatchdogEventKind.HungDetected,
        "first", 0, null, DateTimeOffset.UtcNow);
    WatchdogEvent second = new(Guid.NewGuid(), id, WatchdogEventKind.HungDetected,
        "second", 1, null, DateTimeOffset.UtcNow);
    WatchdogEvent rss = new(Guid.NewGuid(), null, WatchdogEventKind.RssBreached,
        "rss", 0, 5000.5, DateTimeOffset.UtcNow);

    _ = await _store.AppendAsync(first, TestContext.Current.CancellationToken);
    _ = await _store.AppendAsync(second, TestContext.Current.CancellationToken);
    _ = await _store.AppendAsync(rss, TestContext.Current.CancellationToken);

    Result<IReadOnlyList<WatchdogEvent>> recent = await _store.ListRecentAsync(10, TestContext.Current.CancellationToken);
    Assert.True(recent.IsSuccess);
    Assert.Equal(3, recent.Value.Count);
    Assert.Equal(rss.Id, recent.Value[0].Id); // newest first
    WatchdogEvent loadedSecond = recent.Value[1];
    Assert.Equal(WatchdogEventKind.HungDetected, loadedSecond.Kind);
    Assert.Equal(1, loadedSecond.Attempt);
    Assert.Null(loadedSecond.RssMb);
    Assert.Equal(5000.5, recent.Value[0].RssMb);

    Result<int> hungCount = await _store.CountKindForAgentAsync(id, WatchdogEventKind.HungDetected, TestContext.Current.CancellationToken);
    Assert.True(hungCount.IsSuccess);
    Assert.Equal(2, hungCount.Value);

    Result<int> retryCount = await _store.CountKindForAgentAsync(id, WatchdogEventKind.RetrySpawned, TestContext.Current.CancellationToken);
    Assert.True(retryCount.IsSuccess);
    Assert.Equal(0, retryCount.Value);
  }

  [Fact]
  public async Task ListRecent_Limit_BoundsResultsNewestFirst()
  {
    AgentId id = new(Guid.NewGuid());
    for (int i = 0; i < 5; i++)
    {
      _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), id,
          WatchdogEventKind.HungDetected, "evt-" + i, 0, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
      await Task.Delay(5, TestContext.Current.CancellationToken); // created_at resolution
    }

    Result<IReadOnlyList<WatchdogEvent>> recent = await _store.ListRecentAsync(3, TestContext.Current.CancellationToken);
    Assert.True(recent.IsSuccess);
    Assert.Equal(3, recent.Value.Count);
    Assert.Equal("evt-4", recent.Value[0].Detail);
    Assert.Equal("evt-2", recent.Value[2].Detail);
  }

  [Fact]
  public void Migration_CreatesWatchdogEventsTable()
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = _db.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'watchdog_events';";
    object? count = command.ExecuteScalar();
    Assert.Equal(1L, Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture));
  }
}
