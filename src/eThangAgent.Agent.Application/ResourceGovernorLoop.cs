namespace eThangAgent.Agent.Application;

/// <summary>The governor's fast clock (spec D4): one tick per configured interval,
///     ending cleanly on cancellation - the same contract as <see cref="WatchdogLoop"/>.
///     A tick that throws is absorbed: the next tick still runs, because a governor
///     that dies on one bad observation governs nothing.</summary>
public sealed class ResourceGovernorLoop(ResourceGovernor governor, TimeSpan interval, TimeProvider clock)
{
  public async Task RunAsync(CancellationToken ct)
  {
    using PeriodicTimer timer = new(interval, clock);
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

      try
      {
        _ = await governor.TickAsync(ct).ConfigureAwait(false);
      }
      // Named decision (CA1031): an observation or enactment fault must not kill the
      // loop - the governor's whole job is to keep watching under pressure.
#pragma warning disable CA1031 // Do not catch general exception types
      catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031 // Do not catch general exception types
      {
        _ = ex;
      }
    }
  }
}
