using System.Collections.Concurrent;
using eThangAgent.AgentDomain;

namespace eThangAgent.AgentInfrastructure;

/// <summary>Thread-safe in-memory heartbeat over an injected TimeProvider. Process-lifetime
///     singleton per session container; entries are keyed by agent id.</summary>
public sealed class InMemoryAgentHeartbeat(TimeProvider time) : IAgentHeartbeat
{
  private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
  private readonly ConcurrentDictionary<Guid, DateTimeOffset> _beats = [];

  public void Beat(AgentId agentId) => _beats[agentId.Value] = _time.GetUtcNow();

  public bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat)
      => _beats.TryGetValue(agentId.Value, out lastBeat);

  public void Forget(AgentId agentId) => _beats.TryRemove(agentId.Value, out _);
}
