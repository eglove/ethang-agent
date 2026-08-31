namespace eThangAgent.Agent.Application;

/// <summary>Anything the WatchdogLoop can tick. Keeps the loop testable without a full watchdog.</summary>
public interface IWatchdogTicker
{
  Task TickAsync(CancellationToken ct = default);
}
