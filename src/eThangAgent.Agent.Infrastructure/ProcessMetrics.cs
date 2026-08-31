using System.Diagnostics;
using eThangAgent.Agent.Application;

namespace eThangAgent.AgentInfrastructure;

/// <summary>Working set of the current process. The app is the agent host: every child
///     runs in-process, so this single number is the RSS the maintenance plan item asks to
///     observe.</summary>
public sealed class ProcessMetrics : IProcessMetrics
{
  public long WorkingSetBytes() => Process.GetCurrentProcess().WorkingSet64;
}
