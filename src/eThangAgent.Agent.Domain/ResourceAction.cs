namespace eThangAgent.AgentDomain;

/// <summary>One rung of the escalation ladder (spec D3). Rungs are enacted in
///     <see cref="ResourcePolicy.Ladder"/> order and never skipped within an episode.</summary>
public enum ResourceAction
{
  None,
  Steer,
  KillProgram,
  ReclaimMemory,
  RestartConversation,
  RestartApp,
}
