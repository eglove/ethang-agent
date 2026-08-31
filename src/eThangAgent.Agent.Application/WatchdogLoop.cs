using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Process-lifetime ticker host: one PeriodicTimer loop fanning each tick out to
///     every attached session watchdog. Attach/detach are called by the host as tabs open
///     and close; RunAsync ends cleanly on cancellation.</summary>
public sealed class WatchdogLoop(TimeSpan interval, TimeProvider clock)
{
  private readonly Dictionary<Guid, IWatchdogTicker> _tickers = [];
  private readonly Lock _gate = new();

  public IReadOnlyCollection<Guid> AttachedRoots
  {
    get
    {
      lock (_gate)
      {
        return [.. _tickers.Keys];
      }
    }
  }

  public void Attach(AgentId rootId, IWatchdogTicker ticker)
  {
    lock (_gate)
    {
      _tickers[rootId.Value] = ticker;
    }
  }

  public void Detach(AgentId rootId)
  {
    lock (_gate)
    {
      _ = _tickers.Remove(rootId.Value);
    }
  }

  public async Task RunAsync(CancellationToken ct)
  {
    using PeriodicTimer timer = new(interval, clock);
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

      IWatchdogTicker[] snapshot;
      lock (_gate)
      {
        snapshot = [.. _tickers.Values];
      }
      foreach (IWatchdogTicker ticker in snapshot)
      {
        if (ct.IsCancellationRequested)
        {
          return;
        }

        try
        {
          await ticker.TickAsync(ct).ConfigureAwait(false);
        }
        // Named decision (CA1031): the loop outlives any single watchdog failure.
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
        {
          // Contained: AgentWatchdog records its own WatchdogErrored; anything escaping
          // it still must not kill the loop.
        }
      }
    }
  }
}
