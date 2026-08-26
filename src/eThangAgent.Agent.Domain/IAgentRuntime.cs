using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>Seam for starting persisted children as independent actors. Implemented in infrastructure; faked in tests.</summary>
public interface IAgentRuntime
{
  /// <summary>Begins background execution of an already-persisted Running record. Fails with CapReached when at capacity.</summary>
  Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default);

  /// <summary>Interrupts active child runs by cancelling the token each run executes under.
  /// With no id, every active run owned by this runtime is interrupted; with an id, only that
  /// run. Unknown ids are a no-op — interruption is best-effort by design.</summary>
  void Interrupt(AgentId? childId = null);
}
