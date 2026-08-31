using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The loop ticks every attached watchdog once per period, ends cleanly on
///     cancellation, detaches stop ticking, and a throwing ticker never kills the loop.</summary>
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

  [Fact]
  public async Task RunAsync_TicksAttachedWatchdogs_ThenCancelsCleanly()
  {
    WatchdogLoop loop = new(TimeSpan.FromMilliseconds(20), TimeProvider.System);
    CountingTicker a = new();
    CountingTicker b = new();
    loop.Attach(AgentId.NewId(), a);
    loop.Attach(AgentId.NewId(), b);
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(150));

    await loop.RunAsync(cts.Token);

    Assert.True(a.Ticks >= 2);
    Assert.True(b.Ticks >= 2);
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
    WatchdogLoop loop = new(TimeSpan.FromMilliseconds(20), TimeProvider.System);
    loop.Attach(AgentId.NewId(), new ThrowingTicker());
    CountingTicker good = new();
    loop.Attach(AgentId.NewId(), good);
    using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(150));

    await loop.RunAsync(cts.Token); // must not throw

    Assert.True(good.Ticks >= 2);
  }
}
