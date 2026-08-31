namespace eThangAgent.Agent.Application;

/// <summary>Process metrics seam (observe-only): the watchdog samples the app's working
///     set and records breaches. Nothing acts on the value.</summary>
public interface IProcessMetrics
{
  long WorkingSetBytes();
}
