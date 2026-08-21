using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Seam for starting persisted children as independent actors. Implemented in infrastructure; faked in tests.</summary>
public interface IAgentRuntime
{
    /// <summary>Begins background execution of an already-persisted Running record. Fails with CapReached when at capacity.</summary>
    Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default);
}
