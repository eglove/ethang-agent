using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Start command of the spawn CQRS split: validates a spawn request against the parent,
///     persists a Running child, and hands it to the runtime as an independent actor.
///     Implemented by StartSpawnHandler; faked in tests.</summary>
public interface IAgentSpawnCommand
{
  Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default);
}
