namespace eThangAgent.StateDomain;

/// <summary>Bounds for one goal/check command. Generous by default: this repo's own
///     gate runs a full build, a format verify, and the whole test suite, and a gate
///     that times out mid-suite reports a failure the project does not have.</summary>
public sealed record GoalRunnerOptions
{
  public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

  public static GoalRunnerOptions Default { get; } = new();
}
