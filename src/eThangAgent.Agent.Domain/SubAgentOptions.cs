namespace eThangAgent.AgentDomain;

/// <summary>Budgets and defaults governing child agent runs. Absent DefaultModel means
///     spawns must supply their own model. There is NO wall-clock child timeout (FR-L4):
///     cancellation sources are exactly user/parent interrupt, watchdog terminal decision,
///     and budget hard ceiling — never duration (A4).</summary>
public sealed class SubAgentOptions
{
  public string? DefaultModel { get; }
  public int MaxConcurrentAgents { get; }
  public int MaxDepth { get; }

  public SubAgentOptions(string? DefaultModel,
      int MaxConcurrentAgents = 1, int MaxDepth = 3)
  {
    this.DefaultModel = DefaultModel;

    if (MaxConcurrentAgents < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAgents),
          "MaxConcurrentAgents must be at least 1.");
    }

    this.MaxConcurrentAgents = MaxConcurrentAgents;

    if (MaxDepth < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxDepth), "MaxDepth must be at least 1.");
    }

    this.MaxDepth = MaxDepth;
  }
}
