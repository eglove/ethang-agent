namespace eThangAgent.AgentDomain;

/// <summary>A chain step's EXECUTION status (issue #300 D3): what happened, not what
///     should be true. The plan step owns the intent; this owns the record. A plan step
///     marked Done with three failed chain attempts is a coherent, truthful state, and
///     collapsing the two would lose it.</summary>
public enum ChainStepStatus
{
  Pending,
  Running,
  Done,
  Failed,
  Skipped,
  Parked,
}
