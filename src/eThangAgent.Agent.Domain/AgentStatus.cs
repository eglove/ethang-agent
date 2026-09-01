namespace eThangAgent.AgentDomain;

public enum AgentStatus
{
  Running,
  Completed,
  Failed,

  /// <summary>Terminal: cancelled by a user/parent subtree interrupt (FR-C6) — distinct
  ///     from Failed(Interrupted), which marks a run interrupted mid-turn by its stop.
  ///     Kept separate so resume surfaces and audits can tell parent-driven tree teardown
  ///     from a self-interrupted turn.</summary>
  Interrupted,
}

public enum AgentFailureReason
{
  MaxIterations,
  Timeout,
  ProviderError,

  /// <summary>Cancelled explicitly by the user (distinct from the run's own timeout budget).</summary>
  Interrupted,

  /// <summary>Terminated by the watchdog after idle detection and a wrap-up retry.</summary>
  Hung,

  /// <summary>Terminated at a budget hard ceiling (FR-B4/D8: resources, never time).</summary>
  BudgetExhausted,
}
