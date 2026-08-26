namespace eThangAgent.AgentDomain;

/// <summary>Runs a child's full conversation loop to completion. No validation, no persistence — the caller owns those.</summary>
public interface IAgentRunner
{
  Task<AgentRunOutcome> RunAsync(AgentRecord child, CancellationToken ct = default);
}
