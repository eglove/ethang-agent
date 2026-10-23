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

  /// <summary>Non-terminal, owned: the spawn was persisted and admitted to the runtime's
  ///     concurrency-boundary queue, but no run has begun yet (issue #238). A queued child
  ///     is never a silent drop — agent.wait blocks on it instead of failing NotFound,
  ///     agent.send reaches its mailbox, agent.cancel removes it with a well-formed
  ///     Failed(Interrupted) outcome, and orphan repair terminalizes it when no live owner
  ///     claims it. Appended last so the persisted integer values of the original members
  ///     never shift.</summary>
  Queued,
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

  /// <summary>Final report failed schema validation after the one repair round (approved D3).</summary>
  InvalidResult,
}
