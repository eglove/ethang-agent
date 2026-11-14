using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Process-lifetime RSS watch: samples the host process working set each tick and
///     appends rate-limited RssBreached audit rows for as long as the breach holds. A breach
///     sustained over <see cref="WatchdogOptions.RssSustainedBreachTicks"/> consecutive ticks
///     adds one RssSustained row per episode and ESCALATES to the user through
///     <c>escalate</c>. Recovery resets sustain state, so a new episode can escalate again.
///     The plan bullet this serves asks for a maintenance process that polls app RSS
///     independently of open sessions: this monitor lives at process scope, not inside any
///     per-session watchdog.
///     <para>THE ESCALATION IS THE POINT, NOT THE ROW. Nothing reads the audit table, so a
///     monitor that only appended reported a problem it was structurally unable to surface:
///     the shell reached 6.4 GB while the table held hundreds of breach rows and the screen
///     said nothing (issue #382). The callback is how a sustained breach becomes something a
///     person sees; the rows remain the durable record behind it.</para></summary>
/// <param name="escalate">Receives one message per sustained episode. Null means the caller
///     has no user to reach, and the rows are the whole of the record.</param>
/// <param name="metrics">The working-set source.</param>
/// <param name="events">The audit store the breach and sustain rows are appended to.</param>
/// <param name="options">The threshold, re-report interval, and sustain length.</param>
/// <param name="clock">The time source for the rate limit and the row timestamps.</param>
public sealed class ProcessRssMonitor(
    IProcessMetrics metrics,
    IWatchdogEventStore events,
    WatchdogOptions options,
    TimeProvider clock,
    Action<string>? escalate = null)
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
      string detail = "working set has exceeded "
          + options.RssThresholdMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
          + " MB for " + _sustainTicks + " consecutive ticks";
      await AppendAsync(new WatchdogEvent(Guid.NewGuid(), null, WatchdogEventKind.RssSustained,
          detail, 0, Math.Round(megabytes, 1), now), ct).ConfigureAwait(false);

      // The user-facing half. A monitor whose only output is a table nobody reads has
      // observed the problem and reported it to no one.
      escalate?.Invoke("The app is using " + megabytes.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
          + " MB of memory and has stayed above its "
          + options.RssThresholdMb.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
          + " MB limit for " + _sustainTicks + " checks in a row.");
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
