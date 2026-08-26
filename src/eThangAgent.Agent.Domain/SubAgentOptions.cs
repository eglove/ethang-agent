namespace eThangAgent.AgentDomain;

/// <summary>Budgets and defaults governing child agent runs. Absent DefaultModel means spawns must supply their own model.</summary>
public sealed class SubAgentOptions
{
  public string? DefaultModel { get; }
  public TimeSpan ChildTimeout { get; }
  public int MaxConcurrentAgents { get; }
  public int MaxDepth { get; }

  public SubAgentOptions(string? DefaultModel, TimeSpan? ChildTimeout = null,
      int MaxConcurrentAgents = 1, int MaxDepth = 3)
  {
    this.DefaultModel = DefaultModel;

    if (MaxConcurrentAgents < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAgents),
          "MaxConcurrentAgents must be at least 1.");
    }

    this.MaxConcurrentAgents = MaxConcurrentAgents;

    if (ChildTimeout is { } timeout)
    {
      if (timeout <= TimeSpan.Zero)
      {
        throw new ArgumentOutOfRangeException(nameof(ChildTimeout),
            "ChildTimeout must be positive.");
      }

      this.ChildTimeout = timeout;
    }
    else
    {
      this.ChildTimeout = TimeSpan.FromSeconds(300);
    }

    if (MaxDepth < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxDepth), "MaxDepth must be at least 1.");
    }

    this.MaxDepth = MaxDepth;
  }
}
