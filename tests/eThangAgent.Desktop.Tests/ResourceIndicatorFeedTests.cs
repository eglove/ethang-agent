using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The indicator's feed - the audit trail's reader (spec D14). The feed turns
///     one governor tick into the numbers and words the chrome shows, and it is the
///     only thing that reads the governor's decisions back out of the audit table.
///     <para>Three properties matter here and none of them had a test before: the feed
///     reads EVERY kind of decision the governor writes (not just the two the trail
///     happened to carry), it presents them newest-first and bounded, and it updates
///     the bound view-model ON THE UI THREAD - the indicator is bound to the chrome,
///     and a cross-thread property change is a live binding fault the moment the
///     store's read suspends.</para></summary>
public sealed class ResourceIndicatorFeedTests
{
  private static readonly DateTimeOffset Base = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

  private static ResourceGovernorTick Tick() => new(
      ResourceLevel.Normal, null, ResourceAction.None, 0, 8_000_000_000L, TotalBytes: 8_000_000_000L);

  private static WatchdogEvent Row(WatchdogEventKind kind, string marker, int minute)
      => new(Guid.NewGuid(), null, kind,
          "conversation " + marker + " at 100 MB of its 200 MB share (Notice; harness budget 200 MB) -> Steer",
          0, 100, Base.AddMinutes(minute));

  [AvaloniaFact]
  public async Task PublishAsync_ReadsTheDecisionsBackByKind()
  {
    ResourceIndicatorViewModel vm = new();
    FakeStore store = new();
    WatchdogEventKind[] kinds =
    [
      WatchdogEventKind.ResourceNotice,
      WatchdogEventKind.ResourceActed,
      WatchdogEventKind.ResourceEmergency,
      WatchdogEventKind.ResourceRecovered,
      WatchdogEventKind.WatchdogErrored,
    ];
    foreach (WatchdogEventKind kind in kinds)
    {
      store.Rows[kind] = [Row(kind, "c-" + kind, 1)];
    }

    ResourceIndicatorFeed feed = new(vm, new ResourceHostObservation(() => []), store);
    await feed.PublishAsync(Tick(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Pump();

    // Every kind the governor writes reaches the user: the four resource decisions
    // and the refusal row, which must not be swallowed.
    Assert.Equal(kinds.Length, vm.RecentDecisions.Count);
    foreach (WatchdogEventKind kind in kinds)
    {
      Assert.Contains(vm.RecentDecisions, line => line.Contains("c-" + kind, StringComparison.Ordinal));
    }
  }

  [AvaloniaFact]
  public async Task PublishAsync_OrdersDecisionsNewestFirst_AndCapsAtEight()
  {
    ResourceIndicatorViewModel vm = new();
    FakeStore store = new();
    List<WatchdogEvent> rows = [];
    for (int i = 1; i <= 12; i++)
    {
      rows.Add(Row(WatchdogEventKind.ResourceNotice,
          "row-" + i.ToString("00", CultureInfo.InvariantCulture), i));
    }

    store.Rows[WatchdogEventKind.ResourceNotice] = rows;
    ResourceIndicatorFeed feed = new(vm, new ResourceHostObservation(() => []), store);
    await feed.PublishAsync(Tick(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Pump();

    // Newest first, and bounded: the trail is a window, not a ledger.
    Assert.Equal(8, vm.RecentDecisions.Count);
    Assert.Contains("row-12", vm.RecentDecisions[0], StringComparison.Ordinal);
    Assert.Contains("row-05", vm.RecentDecisions[7], StringComparison.Ordinal);
  }

  [AvaloniaFact]
  public async Task PublishAsync_StoreFault_YieldsAnEmptyTrail()
  {
    ResourceIndicatorViewModel vm = new();
    FakeStore store = new();
    store.Rows[WatchdogEventKind.ResourceNotice] = [Row(WatchdogEventKind.ResourceNotice, "c1", 1)];
    ResourceIndicatorFeed feed = new(vm, new ResourceHostObservation(() => []), store);

    await feed.PublishAsync(Tick(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Pump();
    _ = Assert.Single(vm.RecentDecisions);

    // A store fault yields an EMPTY trail, never the previous one: decisions that
    // have since been superseded must not stay on screen.
    store.Fault = true;
    await feed.PublishAsync(Tick(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Pump();

    Assert.Empty(vm.RecentDecisions);
  }

  [AvaloniaFact]
  public void PublishAsync_UpdatesOnTheUIThread()
  {
    ResourceIndicatorViewModel vm = new();
    FakeStore store = new();
    store.Rows[WatchdogEventKind.ResourceNotice] = [Row(WatchdogEventKind.ResourceNotice, "c1", 1)];
    ResourceIndicatorFeed feed = new(vm, new ResourceHostObservation(() => []), store);

    bool? onUiThread = null;
    vm.PropertyChanged += (_, _) => onUiThread ??= Dispatcher.UIThread.CheckAccess();

    // The tick arrives on the governor's background clock, and the store's read
    // suspends: the update must still land on the UI thread, because the indicator
    // is bound to the chrome.
    Task publish = Task.Run(async () =>
        await feed.PublishAsync(Tick(), TestContext.Current.CancellationToken).ConfigureAwait(false));

    // The headless dispatcher only runs when it is PUMPED, so the test pumps it
    // rather than awaiting the publish. The loop is BOUNDED: a feed that never
    // posts fails the assertion instead of hanging the suite.
    bool updated = false;
    for (int i = 0; i < 5000 && !updated; i++)
    {
      Dispatcher.UIThread.RunJobs();
      updated = onUiThread is not null;
      if (!updated)
      {
        _ = Thread.Yield();
      }
    }

    Assert.True(publish.IsCompleted, "the feed never completed its publish");
    Assert.True(updated, "the indicator was never updated");
    Assert.True(onUiThread, "the indicator must be updated on the UI thread");
  }

  /// <summary>Runs the dispatcher queue. The feed POSTS the update to the UI thread,
  ///     so a test that does not pump never sees it - and the headless dispatcher only
  ///     runs when it is pumped.</summary>
  private static void Pump()
  {
    for (int i = 0; i < 50; i++)
    {
      Dispatcher.UIThread.RunJobs();
    }
  }

  /// <summary>A store that hands out the rows it is given, and fails on demand.</summary>
  private sealed class FakeStore : IWatchdogEventStore
  {
    public Dictionary<WatchdogEventKind, List<WatchdogEvent>> Rows { get; } = [];

    public bool Fault { get; set; }

    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
        => Task.FromResult(Result.Success("ok"));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(
        int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public async Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
    {
      // The read SUSPENDS, exactly like a real store's: the continuation then runs on
      // a thread-pool thread unless the feed marshals the update back.
      await Task.Yield();
      if (Fault)
      {
        return Result.Failure<IReadOnlyList<WatchdogEvent>>(
            new DomainError("StoreUnavailable", "the store could not be read."));
      }

      // The store's contract: the rows come back NEWEST FIRST.
      List<WatchdogEvent> rows = Rows.TryGetValue(kind, out List<WatchdogEvent>? found)
          ? [.. found.OrderByDescending(row => row.CreatedAt)]
          : [];
      return Result.Success<IReadOnlyList<WatchdogEvent>>([.. rows.Take(limit)]);
    }

    public Task<Result<int>> CountKindForAgentAsync(
        AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
        => Task.FromResult(Result.Success(0));
  }
}
