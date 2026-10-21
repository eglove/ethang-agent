namespace eThangAgent.AgentDomain;

/// <summary>Pure resource decision policy (spec D2/D3): usage and trend in, level and
///     next rung out. No I/O, no clock - the caller hands in computed facts, so the
///     policy is fully unit-testable and enactment stays in the application layer.
///     The emergency level additionally requires a RISING trend: a stable high-water
///     mark is pressure, not a runaway, and restarting a conversation that is merely
///     large would destroy work for nothing.</summary>
public static class ResourcePolicy
{
  public const double NoticeFraction = 0.60;
  public const double ActFraction = 0.85;
  public const double EmergencyFraction = 0.95;

  /// <summary>The escalation ladder, in enactment order (spec D3).</summary>
  public static IReadOnlyList<ResourceAction> Ladder { get; } =
  [
    ResourceAction.Steer,
    ResourceAction.KillProgram,
    ResourceAction.ReclaimMemory,
    ResourceAction.RestartConversation,
    ResourceAction.RestartApp,
  ];

  /// <summary>The level for one observation. <paramref name="rising"/> means usage grew
  ///     since the previous sample of the same episode.</summary>
  public static ResourceLevel LevelFor(long usage, long budget, bool rising)
  {
    if (budget <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(budget), budget, "A budget must be positive.");
    }

    double fraction = (double)usage / budget;
    return (fraction, rising) switch
    {
      ( >= EmergencyFraction, true) => ResourceLevel.Emergency,
      ( >= ActFraction, _) => ResourceLevel.Act,
      ( >= NoticeFraction, _) => ResourceLevel.Notice,
      _ => ResourceLevel.Normal,
    };
  }

  /// <summary>The next rung to enact for this level: the first ladder entry not yet
  ///     attempted in the current episode. Normal pressure enacts nothing.</summary>
  public static ResourceAction NextAction(ResourceLevel level, IReadOnlyCollection<ResourceAction> attempted)
  {
    ArgumentNullException.ThrowIfNull(attempted);
    if (level == ResourceLevel.Normal)
    {
      return ResourceAction.None;
    }

    // The first rung not yet attempted; an exhausted ladder answers with the last rung.
    return Ladder
        .Where(action => !attempted.Contains(action))
        .DefaultIfEmpty(ResourceAction.RestartApp)
        .First();
  }
}
