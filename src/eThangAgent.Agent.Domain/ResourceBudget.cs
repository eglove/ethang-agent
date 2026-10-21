namespace eThangAgent.AgentDomain;

/// <summary>The machine-derived memory budget the harness enforces (spec D1). Pure:
///     physical memory and the number of open conversations in, thresholds out. The
///     fractions are the spec's named defaults - half the machine for the harness,
///     split evenly across open conversations, a quarter of the total for any one
///     spawned program. Invalid input throws at the boundary; nothing is clamped.</summary>
public sealed record ResourceBudget
{
  /// <summary>Fraction of physical memory the harness may use in total.</summary>
  public const double TotalFraction = 0.50;

  /// <summary>Fraction of the total budget any single spawned program may use.</summary>
  public const double PerSpawnFraction = 0.25;

  public long TotalBytes { get; }
  public long PerConversationBytes { get; }
  public long PerSpawnBytes { get; }
  public int OpenConversations { get; }

  public ResourceBudget(long physicalBytes, int openConversations)
  {
    if (physicalBytes <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(physicalBytes), physicalBytes,
          "Physical memory must be positive.");
    }

    if (openConversations < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(openConversations), openConversations,
          "At least one conversation must be open.");
    }

    OpenConversations = openConversations;
    TotalBytes = (long)(physicalBytes * TotalFraction);
    PerConversationBytes = TotalBytes / openConversations;
    PerSpawnBytes = (long)(TotalBytes * PerSpawnFraction);
  }
}
