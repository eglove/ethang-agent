namespace eThangAgent.AgentDomain;

/// <summary>Phase of a RUNNING child, written by the runtime at transitions. Null on
///     non-running records — a terminal child has no current phase (P2: facts, not derivations).</summary>
public enum ChildPhase
{
  ModelCall,
  ToolExec,
  Draining,
}
