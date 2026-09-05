using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The process-RSS monitor: a breach row is appended when the working set is
///     over threshold, re-reports are rate-limited, recovery resets sustain state, and a
///     breach sustained over RssSustainedBreachTicks ticks appends exactly one
///     RssSustained row per episode. A failing event store never kills the tick.</summary>
public class ProcessRssMonitorTests
{
  private static readonly DateTimeOffset T0 = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

  private sealed class StubClock(DateTimeOffset start) : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  /// <summary>A TimeProvider whose timers fire only when told (same pattern as WatchdogLoopTests).</summary>
  private sealed class ManualClock : TimeProvider
  {
    private readonly List<ManualTimer> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
      ManualTimer timer = new(callback, state);
      lock (_timers)
      {
        _timers.Add(timer);
      }
      return timer;
    }

    public void Fire(int times)
    {
      for (int i = 0; i < times; i++)
      {
        ManualTimer[] snapshot;
        lock (_timers)
        {
          snapshot = [.. _timers];
        }
        foreach (ManualTimer timer in snapshot)
        {
          timer.Fire();
        }
      }
    }
  }

  private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
  {
    public void Fire() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period) => true;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
      Dispose();
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeMetrics(long bytes) : IProcessMetrics
  {
    public long Bytes { get; set; } = bytes;
    public long WorkingSetBytes() => Bytes;
  }

  private sealed class FakeEvents : IWatchdogEventStore
  {
    public List<WatchdogEvent> Appended { get; } = [];
    public bool Fail { get; set; }

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
    {
      if (Fail)
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("StoreUnavailable", "injected failure")));
      }

      Appended.Add(evt);
      return Task.FromResult(Result.Success(evt.Id.ToString()));
    }

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([.. Appended.Take(limit)]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
      => Task.FromResult(Result.Success(Appended.Count(e => e.AgentId == agentId && e.Kind == kind)));
  }

  private static (ProcessRssMonitor Monitor, FakeMetrics Metrics, StubClock Clock, FakeEvents Events) Harness(
      long workingSetBytes, StubClock clock, int? sustainedTicks = null)
  {
    FakeMetrics metrics = new(workingSetBytes);
    FakeEvents events = new();
    ProcessRssMonitor monitor = new(
        metrics,
        events,
        sustainedTicks is { } t
            ? new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60), RssSustainedBreachTicks: t)
            : new WatchdogOptions(TickInterval: TimeSpan.FromSeconds(60)),
        clock);
    return (monitor, metrics, clock, events);
  }

  [Fact]
  public async Task Tick_BelowThreshold_NoRows()
  {
    (ProcessRssMonitor monitor, _, _, FakeEvents events) = Harness(1024L * 1024 * 1024, new StubClock(T0));
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    Assert.Empty(events.Appended);
  }

  [Fact]
  public async Task Tick_Breach_RecordsProcessScopedRowWithMeasuredMb()
  {
    (ProcessRssMonitor monitor, _, _, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0));
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    WatchdogEvent evt = Assert.Single(events.Appended);
    Assert.Equal(WatchdogEventKind.RssBreached, evt.Kind);
    Assert.Null(evt.AgentId); // process scope, never one agent
    Assert.Equal(5000.0, evt.RssMb);
    Assert.Contains("5000", evt.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Tick_BreachRepeatedWithoutClockAdvance_RateLimited()
  {
    (ProcessRssMonitor monitor, _, _, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0));
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    _ = Assert.Single(events.Appended, e => e.Kind == WatchdogEventKind.RssBreached);
    Assert.DoesNotContain(events.Appended, e => e.Kind == WatchdogEventKind.RssSustained);
  }

  [Fact]
  public async Task Tick_BreachPastReReportInterval_ReportsAgain()
  {
    (ProcessRssMonitor monitor, _, StubClock clock, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0));
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    clock.Now = T0 + TimeSpan.FromMinutes(11);
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    Assert.Equal(2, events.Appended.Count(e => e.Kind == WatchdogEventKind.RssBreached));
  }

  [Fact]
  public async Task Tick_BreachSustainedOverThresholdTicks_OneSustainedRowPerEpisode()
  {
    (ProcessRssMonitor monitor, _, _, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0), sustainedTicks: 2);
    await monitor.TickAsync(TestContext.Current.CancellationToken); // breach 1: reported
    await monitor.TickAsync(TestContext.Current.CancellationToken); // breach 2: rate-limited, reaches threshold -> sustained
    await monitor.TickAsync(TestContext.Current.CancellationToken); // breach 3: still sustained, already fired this episode
    Assert.Equal(2, events.Appended.Count); // one breach report + one sustained marker
    _ = Assert.Single(events.Appended, e => e.Kind == WatchdogEventKind.RssBreached);
    _ = Assert.Single(events.Appended, e => e.Kind == WatchdogEventKind.RssSustained);
  }

  [Fact]
  public async Task Tick_RecoveryThenNewBreach_NewEpisodeCanSustainAgain()
  {
    (ProcessRssMonitor monitor, FakeMetrics metrics, _, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0), sustainedTicks: 2);
    await monitor.TickAsync(TestContext.Current.CancellationToken); // episode 1 breach
    await monitor.TickAsync(TestContext.Current.CancellationToken); // episode 1 sustained
    metrics.Bytes = 1024L * 1024 * 1024;
    await monitor.TickAsync(TestContext.Current.CancellationToken); // recovered
    metrics.Bytes = 5000L * 1024 * 1024;
    await monitor.TickAsync(TestContext.Current.CancellationToken); // episode 2 breach 1
    await monitor.TickAsync(TestContext.Current.CancellationToken); // episode 2 breach 2 -> sustained again
    Assert.Equal(2, events.Appended.Count(e => e.Kind == WatchdogEventKind.RssSustained));
  }

  [Fact]
  public async Task Tick_StoreFailure_DoesNotThrowAndDoesNotKillMonitor()
  {
    (ProcessRssMonitor monitor, FakeMetrics metrics, _, FakeEvents events) = Harness(5000L * 1024 * 1024, new StubClock(T0));
    events.Fail = true;
    await monitor.TickAsync(TestContext.Current.CancellationToken);
    events.Fail = false;
    metrics.Bytes = 1024L * 1024 * 1024;
    await monitor.TickAsync(TestContext.Current.CancellationToken); // still alive, nothing recorded below threshold
    Assert.Empty(events.Appended);
  }

  [Fact]
  public async Task RunAsync_TicksAndEndsCleanlyOnCancel()
  {
    ManualClock clock = new();
    FakeMetrics metrics = new(5000L * 1024 * 1024);
    FakeEvents events = new();
    ProcessRssMonitor monitor = new(metrics, events,
        new WatchdogOptions(TickInterval: TimeSpan.FromMilliseconds(20)), clock);
    using CancellationTokenSource cts = new();

    Task run = monitor.RunAsync(cts.Token); // synchronous start: the timer is armed before this returns
    clock.Fire(3); // three scheduled ticks
    await TicksDeliveredAsync(events, 1).ConfigureAwait(true); // breach reported on the first completed tick

    await cts.CancelAsync().ConfigureAwait(true);
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true); // bounded: clean end is the contract
    _ = Assert.Single(events.Appended, e => e.Kind == WatchdogEventKind.RssBreached); // rate-limited: exactly one report for all three ticks
  }

  /// <summary>Bounded scheduling grace for a fired beat to reach its append — a deadline,
  ///     not a gate: the tick itself is guaranteed by the loop's contract.</summary>
  private static async Task TicksDeliveredAsync(FakeEvents events, int expected)
  {
    for (int attempt = 0; attempt < 200 && events.Appended.Count < expected; attempt++)
    {
      await Task.Delay(10).ConfigureAwait(true);
    }
  }
}
