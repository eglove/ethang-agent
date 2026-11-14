using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>A repeating identical watchdog event is ONE row (issue #386).
///     <para>THE DEFECT WAS DUPLICATION CREATED AT WRITE TIME, so the fix is there and the
///     assertions are on the table's own counts: 1,583 rows held 516 distinct details on the
///     measured database, and one orphan-repair line alone accounted for 596 of them. An
///     age-based sweep would delete the older 500 and leave the 596th to be written tomorrow, so
///     what these tests measure is the distinct-to-total ratio moving to 1:1.</para></summary>
public sealed class WatchdogEventCoalescingTests : IDisposable
{
  private const string OrphanRepair = "orphan repair: Running with no live owner -> Failed(Interrupted)";

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly AppDatabase _db;
  private readonly SqliteWatchdogEventStore _store;

  public WatchdogEventCoalescingTests()
  {
    _db = new AppDatabase(_dbPath);
    _store = new SqliteWatchdogEventStore(_db);
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

  /// <summary>The measured case, replayed: the same line appended many times inside the window
  ///     leaves ONE row whose count is the number of times it was recorded.</summary>
  [Fact]
  public async Task TheSameLineRecordedManyTimes_IsOneRowWithThatCount()
  {
    AgentId agent = AgentId.NewId();
    DateTimeOffset at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    for (int i = 0; i < 50; i++)
    {
      _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
          WatchdogEventKind.TerminalReport, OrphanRepair, 0, null, at.AddSeconds(i)),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM watchdog_events;").ConfigureAwait(true));
    Result<IReadOnlyList<WatchdogEvent>> recent = await _store.ListRecentAsync(10,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(recent.IsSuccess, recent.Error?.Message);
    WatchdogEvent row = Assert.Single(recent.Value);
    Assert.Equal(50, row.Occurrences);
    Assert.Equal(at.AddSeconds(49), row.LastSeen);
    Assert.Equal(at, row.CreatedAt);
  }

  /// <summary>The count is what a retry attempt is derived from, so a coalesced RetrySpawned
  ///     event must still contribute every occurrence. Reading rows instead of their counts would
  ///     silently cap a retried agent at one attempt.</summary>
  [Fact]
  public async Task CoalescedRetrySpawnedRows_StillCountAsEveryAttempt()
  {
    AgentId agent = AgentId.NewId();
    DateTimeOffset at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    for (int i = 0; i < 3; i++)
    {
      _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
          WatchdogEventKind.RetrySpawned, "retry after a stream stall", 0, null, at.AddSeconds(i)),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    Result<int> attempts = await _store.CountKindForAgentAsync(agent,
        WatchdogEventKind.RetrySpawned, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(attempts.IsSuccess, attempts.Error?.Message);
    Assert.Equal(3, attempts.Value);
  }

  /// <summary>A repeat AFTER the window starts a new row: a condition that keeps recurring over
  ///     hours leaves a trail of when each episode began, and only the bursts inside a window
  ///     collapse.</summary>
  [Fact]
  public async Task ARepeatAfterTheWindow_StartsANewRow()
  {
    AgentId agent = AgentId.NewId();
    DateTimeOffset at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
        WatchdogEventKind.RssBreached, "working set above the ceiling", 0, 4096.0, at),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
        WatchdogEventKind.RssBreached, "working set above the ceiling", 0, 4096.0,
        at.Add(SqliteWatchdogEventStore.CoalescingWindow).AddMinutes(1)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM watchdog_events;").ConfigureAwait(true));
  }

  /// <summary>Two agents recording the same line keep their own rows: the match is the whole
  ///     identity, so one agent's failure never inflates another's count.</summary>
  [Fact]
  public async Task TheSameLine_ForTwoAgents_IsTwoRows()
  {
    DateTimeOffset at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), AgentId.NewId(),
        WatchdogEventKind.TerminalReport, OrphanRepair, 0, null, at),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), AgentId.NewId(),
        WatchdogEventKind.TerminalReport, OrphanRepair, 0, null, at),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM watchdog_events;").ConfigureAwait(true));
  }

  /// <summary>Distinct details never coalesce with each other, which is what keeps the table's
  ///     ratio meaningful rather than trivially one.</summary>
  [Fact]
  public async Task DistinctDetails_StayDistinctRows()
  {
    AgentId agent = AgentId.NewId();
    DateTimeOffset at = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
        WatchdogEventKind.HungDetected, "no heartbeat for 30s", 0, null, at),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await _store.AppendAsync(new WatchdogEvent(Guid.NewGuid(), agent,
        WatchdogEventKind.HungDetected, "no heartbeat for 90s", 0, null, at.AddSeconds(1)),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(2L, await ScalarAsync("SELECT COUNT(*) FROM watchdog_events;").ConfigureAwait(true));
  }

  private async Task<long> ScalarAsync(string sql)
  {
#pragma warning disable CA2007
    await using Microsoft.Data.Sqlite.SqliteConnection connection = _db.Open();
#pragma warning restore CA2007
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
  }
}
