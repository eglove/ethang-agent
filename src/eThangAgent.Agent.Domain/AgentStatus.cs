namespace eThangAgent.AgentDomain;

public enum AgentStatus
{
  Running,
  Completed,
  Failed,
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
}
