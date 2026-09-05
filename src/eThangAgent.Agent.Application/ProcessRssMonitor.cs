using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Process-lifetime RSS watch (observe-only): samples the host process working
///     set each tick and appends rate-limited RssBreached audit rows for as long as the
///     breach holds. A breach sustained over <see cref="WatchdogOptions.RssSustainedBreachTicks"/>
///     consecutive ticks adds one RssSustained row per episode — the durable hook a future
///     force-recycle policy will key off. Recovery resets sustain state, so a new episode
///     can escalate again. The plan bullet this serves asks for a maintenance process that
///     polls app RSS independently of open sessions: this monitor lives at process scope,
///     not inside any per-session watchdog.</summary>
public sealed class ProcessRssMonitor(
    IProcessMetrics metrics,
    IWatchdogEventStore events,
    WatchdogOptions options,
    TimeProvider clock)
{
  private DateTimeOffset? _lastReport;
  private int _sustainTicks;
  private bool _sustainedFired;

  /// <summary>The host timer loop: one observation cycle per fired beat, ending cleanly
  ///     on cancellation — the same contract as <see cref="WatchdogLoop"/>.</summary>
  public async Task RunAsync(CancellationToken ct)
  {
    using PeriodicTimer timer = new(options.TickInterval, clock);
    while (true)
    {
      try
      {
        if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
          return;
        }
      }
      catch (OperationCanceledException)
      {
        return;
      }

      await TickAsync(ct).ConfigureAwait(false);
    }
  }

  /// <summary>One observation cycle. The host's timer loop calls this; tests call it
  ///     directly. Store failures are absorbed best-effort: an audit write is an
  ///     observation, never a decision — the monitor stays alive.</summary>
  public async Task TickAsync(CancellationToken ct = default)
  {
    double megabytes = metrics.WorkingSetBytes() / (1024.0 * 1024.0);
    if (megabytes < options.RssThresholdMb)
    {
      _lastReport = null;
      _sustainTicks = 0;
      _sustainedFired = false;
      return;
    }

    _sustainTicks++;

    DateTimeOffset now = clock.GetUtcNow();
    if (_lastReport is null || now - _lastReport >= options.RssReReportInterval)
    {
      _lastReport = now;
      await AppendAsync(new WatchdogEvent(
          Guid.NewGuid(), null, WatchdogEventKind.RssBreached,
          "working set " + megabytes.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
              + " MB exceeds threshold " + options.RssThresholdMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
          0, Math.Round(megabytes, 1), now), ct).ConfigureAwait(false);
    }

    if (!_sustainedFired && _sustainTicks >= options.RssSustainedBreachTicks)
    {
      _sustainedFired = true;
      await AppendAsync(new WatchdogEvent(
          Guid.NewGuid(), null, WatchdogEventKind.RssSustained,
          "working set has exceeded " + options.RssThresholdMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
              + " MB for " + _sustainTicks + " consecutive ticks",
          0, Math.Round(megabytes, 1), now), ct).ConfigureAwait(false);
    }
  }

  /// <summary>Best-effort by contract, mirroring the session watchdog: a failed event
  ///     write never blocks the next observation.</summary>
  private async Task AppendAsync(WatchdogEvent evt, CancellationToken ct)
  {
    Result<string> ignored = await events.AppendAsync(evt, ct).ConfigureAwait(false);
    _ = ignored;
  }
}
