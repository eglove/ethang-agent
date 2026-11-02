namespace eThangAgent.AgentDomain;

/// <summary>Budgets and defaults governing an agent run. Absent DefaultModel means a run
///     must supply its own model. There is NO wall-clock timeout (FR-L4): cancellation
///     sources are exactly the user's interrupt, the watchdog's terminal decision, and the
///     budget hard ceiling — never duration (A4). There is no depth bound either: concurrency
///     is bounded by host capacity, which is a measurement of the machine rather than a
///     setting (issue #334).</summary>
public sealed class AgentRunOptions
{
  public string? DefaultModel { get; }
  public int MaxConcurrentAgents { get; }

  /// <summary>The root-turn budget the app-side runtime enforces (finding 3: a bound only
  ///     a test constructor could set was not configurable in production). Null means the
  ///     runtime's own default; the value rides settings to the host the same way the
  ///     watchdog knobs do.</summary>
  public RootTurnBudget? RootTurnBudget { get; }

  public AgentRunOptions(string? DefaultModel,
      int MaxConcurrentAgents = 1, RootTurnBudget? RootTurnBudget = null)
  {
    this.RootTurnBudget = RootTurnBudget;
    this.DefaultModel = DefaultModel;

    if (MaxConcurrentAgents < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAgents),
          "MaxConcurrentAgents must be at least 1.");
    }

    this.MaxConcurrentAgents = MaxConcurrentAgents;
  }
}
