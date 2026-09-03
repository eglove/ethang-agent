using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The loop ticks every attached watchdog once per fired clock beat, ends cleanly on
///     cancellation, detaches stop ticking, and a throwing ticker never kills the loop.
///     Time is manual: ticks are deterministic and never race the wall clock, which a
///     loaded parallel test host cannot be asked to guarantee.</summary>
public class WatchdogLoopTests
{
  private sealed class CountingTicker : IWatchdogTicker
  {
    public int Ticks { get; private set; }

    public Task TickAsync(CancellationToken ct = default)
    {
      Ticks++;
      return Task.CompletedTask;
    }
  }

  private sealed class ThrowingTicker : IWatchdogTicker
  {
    public Task TickAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
  }

  /// <summary>A TimeProvider whose timers fire only when told: PeriodicTimer(interval, clock)
  ///     schedules through CreateTimer, so each Fire delivers exactly one tick.</summary>
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

  [Fact]
  public async Task RunAsync_TicksAttachedWatchdogs_ThenCancelsCleanly()
  {
    ManualClock clock = new();
    WatchdogLoop loop = new(TimeSpan.FromMilliseconds(20), clock);
    CountingTicker a = new();
    CountingTicker b = new();
    loop.Attach(AgentId.NewId(), a);
    loop.Attach(AgentId.NewId(), b);
    using CancellationTokenSource cts = new();

    Task run = loop.RunAsync(cts.Token); // synchronous start: the timer is armed before this returns
    clock.Fire(1);
    await TicksDeliveredAsync(b, 1).ConfigureAwait(true); // b ticks last in a cycle — its count proves the full cycle ran
    clock.Fire(1);
    await TicksDeliveredAsync(b, 2).ConfigureAwait(true);

    Assert.Equal(2, a.Ticks); // exactly the fired beats — nothing depends on wall-clock speed
    Assert.Equal(2, b.Ticks);

    await cts.CancelAsync().ConfigureAwait(true);
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true); // bounded: a clean end is the contract
  }

  [Fact]
  public void Detach_StopsTicking()
  {
    WatchdogLoop loop = new(TimeSpan.FromMilliseconds(20), TimeProvider.System);
    AgentId id = AgentId.NewId();
    loop.Attach(id, new CountingTicker());

    loop.Detach(id);

    Assert.DoesNotContain(id.Value, loop.AttachedRoots);
  }

  [Fact]
  public async Task RunAsync_TickerThrowing_ContainedAndLoopContinues()
  {
    ManualClock clock = new();
    WatchdogLoop loop = new(TimeSpan.FromMilliseconds(20), clock);
    loop.Attach(AgentId.NewId(), new ThrowingTicker()); // throws first in every cycle
    CountingTicker good = new();
    loop.Attach(AgentId.NewId(), good);
    using CancellationTokenSource cts = new();

    Task run = loop.RunAsync(cts.Token);
    clock.Fire(1);
    await TicksDeliveredAsync(good, 1).ConfigureAwait(true); // the throw was contained: the good ticker still ticked
    clock.Fire(1);
    await TicksDeliveredAsync(good, 2).ConfigureAwait(true); // and the loop kept ticking after the throw

    Assert.Equal(2, good.Ticks);

    await cts.CancelAsync().ConfigureAwait(true);
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true); // must complete without faulting or cancelling
  }

  /// <summary>Bounded scheduling grace for a fired beat to reach its ticker — a deadline,
  ///     not a gate: the tick itself is guaranteed by the loop's contract.</summary>
  private static async Task TicksDeliveredAsync(CountingTicker ticker, int expected)
  {
    for (int attempt = 0; attempt < 200 && ticker.Ticks < expected; attempt++)
    {
      await Task.Delay(10).ConfigureAwait(true);
    }
  }
}
