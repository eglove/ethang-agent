namespace eThangAgent.AgentDomain;

/// <summary>The chain's lifecycle status (issue #300 D4). The set is the minimum the
///     lifecycle needs: <see cref="Parked"/> is load-bearing rather than decorative,
///     because failure handling parks instead of aborting, so the record must be able to
///     say "waiting" without claiming failure. <see cref="Completed"/> and
///     <see cref="Abandoned"/> are terminal and freeze the record.</summary>
public enum ChainStatus
{
  Running,
  Parked,
  Completed,
  Abandoned,
}
