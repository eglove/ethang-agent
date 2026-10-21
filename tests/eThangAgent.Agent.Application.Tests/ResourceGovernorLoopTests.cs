using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>The governor's fast clock: it ticks the governor once per fired beat, absorbs a
///     throwing tick, and ends cleanly on cancellation without ever touching the governor.
///     Time is manual - ticks are deterministic and never race the wall clock.</summary>
public sealed class ResourceGovernorLoopTests
{
  private const long Physical = 16_000_000_000L; // budget total 8 GB, one conversation

  [Fact]
  public async Task RunAsync_CancelledToken_ReturnsWithoutTick()
  {
    using CancellationTokenSource cts = new();
    await cts.CancelAsync();
    ResourceGovernorLoop loop = new(null!, TimeSpan.FromSeconds(2), TimeProvider.System);

    Task run = loop.RunAsync(cts.Token);

    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true); // a clean end is the contract
    Assert.True(run.IsCompletedSuccessfully); // returned, never faulted - the governor was never touched
  }

  [Fact]
  public void RecommendedInterval_IsTheConfiguredResourceTickInterval()
  {
    // One source of truth: the governor recommends the options value, it never invents one.
    WatchdogOptions options = new(ResourceTickInterval: TimeSpan.FromSeconds(3));
    ResourceGovernor governor = new(new FixedResources(Physical),
        new FixedObservations(new ResourceObservation("c1", 100_000_000L, [])),
        new RecordingEnactment(), new RecordingEvents(), options, TimeProvider.System, () => 1);

    Assert.Equal(TimeSpan.FromSeconds(3), governor.RecommendedInterval);
    Assert.Equal(options.ResourceTickInterval, governor.RecommendedInterval);
  }

  [Fact]
  public async Task RunAsync_FiredBeat_TicksTheGovernor()
  {
    ManualClock clock = new();
    RecordingEnactment enactment = new();
    ResourceGovernor governor = new(new FixedResources(Physical),
        new FixedObservations(new ResourceObservation("c1", 5_000_000_000L, [])),
        enactment, new RecordingEvents(), new WatchdogOptions(), clock, () => 1);
    ResourceGovernorLoop loop = new(governor, governor.RecommendedInterval, clock);
    using CancellationTokenSource cts = new();

    Task run = loop.RunAsync(cts.Token); // synchronous start: the timer is armed before this returns
    clock.Fire(1);
    await CallsDeliveredAsync(enactment, 1).ConfigureAwait(true);

    _ = Assert.Single(enactment.Calls); // exactly the fired beat drove one governor decision

    await cts.CancelAsync().ConfigureAwait(true);
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  [Fact]
  public async Task RunAsync_TickThrowing_ContainedAndLoopContinues()
  {
    ManualClock clock = new();
    RecordingEnactment enactment = new();
    FlakyObservations observations = new(new ResourceObservation("c1", 5_000_000_000L, []));
    ResourceGovernor governor = new(new FixedResources(Physical), observations,
        enactment, new RecordingEvents(), new WatchdogOptions(), clock, () => 1);
    ResourceGovernorLoop loop = new(governor, governor.RecommendedInterval, clock);
    using CancellationTokenSource cts = new();

    Task run = loop.RunAsync(cts.Token);
    clock.Fire(1); // throws inside Observe
    clock.Fire(1); // the loop survived the throw and ticked again
    await CallsDeliveredAsync(enactment, 1).ConfigureAwait(true);

    _ = Assert.Single(enactment.Calls);

    await cts.CancelAsync().ConfigureAwait(true);
    await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>Bounded scheduling grace for a fired beat to reach the governor - a deadline,
  ///     not a gate: the tick itself is guaranteed by the loop's contract.</summary>
  private static async Task CallsDeliveredAsync(RecordingEnactment enactment, int expected)
  {
    for (int attempt = 0; attempt < 200 && enactment.Calls.Count < expected; attempt++)
    {
      await Task.Delay(10).ConfigureAwait(true);
    }
  }

  /// <summary>Throws on the first observation, then behaves - the shape of a transient
  ///     probe fault the loop must survive.</summary>
  private sealed class FlakyObservations(ResourceObservation observation) : IResourceObservationSource
  {
    private int _calls;

    public IReadOnlyList<ResourceObservation> Observe()
      => Interlocked.Increment(ref _calls) == 1
          ? throw new InvalidOperationException("observation fault")
          : [observation];
  }

  private sealed class FixedResources(long bytes) : IMachineResources
  {
    public Result<long> TotalPhysicalBytes() => Result.Success(bytes);
  }

  private sealed class FixedObservations(params ResourceObservation[] observations) : IResourceObservationSource
  {
    public IReadOnlyList<ResourceObservation> Observe() => observations;
  }

  private sealed class RecordingEnactment : IResourceEnactment
  {
    public List<(string ConversationId, ResourceAction Action)> Calls { get; } = [];

    public Task<Result<string>> EnactAsync(string conversationId, ResourceAction action, string reason, CancellationToken ct = default)
    {
      Calls.Add((conversationId, action));
      return Task.FromResult(Result.Success("enacted"));
    }
  }

  private sealed class RecordingEvents : IWatchdogEventStore
  {
    public Task<Result<string>> AppendAsync(WatchdogEvent evt, CancellationToken ct = default)
      => Task.FromResult(Result.Success("ok"));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentAsync(int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<IReadOnlyList<WatchdogEvent>>> ListRecentByKindAsync(
        WatchdogEventKind kind, int limit, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<WatchdogEvent>>([]));

    public Task<Result<int>> CountKindForAgentAsync(AgentId agentId, WatchdogEventKind kind, CancellationToken ct = default)
      => Task.FromResult(Result.Success(0));
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
}
