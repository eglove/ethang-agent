using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.ChildHost;

/// <summary>Host-side idle detection (handoff item 2): one watchdog over ONE child run,
///     living in the host process. Facts come from the child container's own event
///     stream (via the SupervisorFeed), decisions from the same pure policy the app
///     uses, and enactment through the child container's own runtime — the host's
///     policy decides retry/terminal locally and settle envelopes reach the app as
///     usual. Deliberately NOT app-side guessing from absent beats: the app never
///     ships idle facts for remote children (R3 doctrine).</summary>
public sealed class HostChildWatchdog(AgentId childId, WatchdogServices services, TimeSpan? tickInterval = null) : IAsyncDisposable
{
  private readonly AgentWatchdog _watchdog = new(childId, services);
  private readonly WatchdogLoop _loop = new(tickInterval ?? services.Options.TickInterval, services.Clock);
  private readonly CancellationTokenSource _cts = new();

  /// <summary>Begins ticking. Fire-and-forget: a faulting tick is contained by the loop.</summary>
  public void Start()
  {
    _ = Task.Run(() => _loop.RunAsync(_cts.Token), CancellationToken.None);
    _loop.Attach(childId, _watchdog);
  }

  /// <summary>One synchronous tick — the direct seam for focused tests.</summary>
  public Task TickOnceAsync(CancellationToken ct = default) => _watchdog.TickAsync(ct);

  public async Task StopAsync()
  {
    _loop.Detach(childId);
    await _cts.CancelAsync().ConfigureAwait(false);
    _watchdog.Dispose();
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync().ConfigureAwait(false);
    _cts.Dispose();
  }
}
