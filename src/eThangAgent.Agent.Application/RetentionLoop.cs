using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>The app-level retention ticker (issue #246): one sweep per interval for the life of
///     the process, with no model involvement and no tool surface.
///     <para>THE LOOP DRIVES A PORT, so it can be exercised without a database and the storage
///     implementation keeps the window and the table list. A run that reports null means
///     retention is disabled; a run that deleted nothing is an outcome, not a setting.</para>
///     <para>EVERY OUTCOME IS REPORTED THROUGH ONE SEAM. A run that deleted rows and a run that
///     could not must both be visible: a failure silently swallowed would leave a database
///     growing with no account of why.</para></summary>
public sealed class RetentionLoop(IRetentionService retention,
    Action<RetentionRun> onCompleted, Action<string> onFaulted) : IRetentionTicker
{
  /// <summary>The interval between sweeps: once a day, which is far more often than a thirty-day
  ///     window needs and rare enough that a reclaim never competes with a session for the file.
  ///     </summary>
  public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

  private readonly IRetentionService _retention =
      retention ?? throw new ArgumentNullException(nameof(retention));
  private readonly Action<RetentionRun> _onCompleted =
      onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
  private readonly Action<string> _onFaulted =
      onFaulted ?? throw new ArgumentNullException(nameof(onFaulted));

  /// <summary>Runs one sweep, which is the whole of the loop's body and the seam a test drives
  ///     without waiting out a day.</summary>
  /// <param name="ct">Cancellation for the run.</param>
  public async Task RunOnceAsync(CancellationToken ct = default)
  {
    Result<RetentionRun?> run = await _retention.RunOnceAsync(ct).ConfigureAwait(false);
    if (!run.IsSuccess)
    {
      _onFaulted(run.Error.Message);
      return;
    }

    if (run.Value is not { } report)
    {
      return; // retention is disabled: nothing is old, by the user's own instruction
    }

    if (report.ReclaimFailure is { } failure)
    {
      _onFaulted("the retention sweep deleted " + report.RowsDeleted
          + " rows but could not reclaim the file: " + failure);
      return;
    }

    // A run that deleted nothing is not reported: a daily 'nothing happened' line would bury the
    // runs that did something, and the measurement the issue asks for is the one that moved.
    if (report.RowsDeleted > 0)
    {
      _onCompleted(report);
    }
  }

  /// <summary>Runs one sweep per interval for as long as the token is live.</summary>
  /// <param name="ct">Cancellation for the loop.</param>
  public async Task RunAsync(CancellationToken ct)
  {
    using PeriodicTimer timer = new(Interval);
    while (!ct.IsCancellationRequested)
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

      try
      {
        await RunOnceAsync(ct).ConfigureAwait(false);
      }
      // Named decision (CA1031): a sweep that faults must not kill the loop - tomorrow's run is
      // the recovery, and the fault is reported rather than swallowed.
      catch (InvalidOperationException ex)
      {
        _onFaulted(ex.Message);
      }
    }
  }
}

/// <summary>The retention loop as the host sees it, so the loop can be driven without its timer.
///     </summary>
public interface IRetentionTicker
{
  /// <summary>Runs one sweep.</summary>
  /// <param name="ct">Cancellation for the run.</param>
  Task RunOnceAsync(CancellationToken ct = default);
}
