namespace eThangAgent.AgentDomain;

/// <summary>Liveness signals for running agents: the agent loop beats at safe points,
///     the watchdog reads beats to compute idle age, and the watchdog (or the run's own
///     teardown) forgets entries. Process-local by design - liveness is a property of the
///     live runtime, never persisted.</summary>
public interface IAgentHeartbeat
{
  /// <summary>Records a liveness beat for the agent.</summary>
  void Beat(AgentId agentId);

  /// <summary>Reads the most recent beat. False when the agent never beat or was forgotten.</summary>
  bool TryGetLastBeat(AgentId agentId, out DateTimeOffset lastBeat);

  /// <summary>Drops the agent's entry: teardown and watchdog restarts call this so a stale
  ///     beat can never read a fresh run as idle.</summary>
  void Forget(AgentId agentId);
}
