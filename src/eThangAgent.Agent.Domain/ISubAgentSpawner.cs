using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Seam for spawning child agents; implemented by SubAgentSpawner and test fakes.</summary>
public interface ISubAgentSpawner
{
    Task<Result<AgentRunOutcome>> SpawnAsync(AgentRecord parent, SpawnRequest request, CancellationToken ct = default);
}