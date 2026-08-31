using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Collaborators one AgentWatchdog needs. Built per session container by the
///     host; the store, runtime, and heartbeat are that container's own instances.</summary>
public sealed record WatchdogServices(
    IAgentStore Store,
    IAgentRuntime Runtime,
    IAgentHeartbeat Heartbeat,
    IWatchdogEventStore Events,
    WatchdogPolicy Policy,
    IProcessMetrics Metrics,
    WatchdogOptions Options,
    TimeProvider Clock);
