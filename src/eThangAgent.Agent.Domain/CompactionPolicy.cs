namespace eThangAgent.AgentDomain;

/// <summary>The derived compaction pressures for one context window: the ceiling that
/// forces the deep clean, the soft level that fires the one-time reminder, and the
/// utilization target a deep clean lands at. A pure function of the resolved window
/// size — never of a model name, never of configuration (zero-config decision:
/// models change constantly; the harness does not track them by name).</summary>
public sealed record CompactionPolicy(double CeilingPercent, double SoftReminderPercent, double TargetPercent)
{
  public const int SmallWindowMax = 200_000;
  public const int MediumWindowMax = 500_000;
  public const int LargeWindowMax = 1_000_000;

  public static CompactionPolicy For(int? contextWindow) => contextWindow switch
  {
    null or <= 0 => Small,
    <= SmallWindowMax => Small,
    <= MediumWindowMax => Medium,
    <= LargeWindowMax => Large,
    _ => VeryLarge,
  };

  private static readonly CompactionPolicy Small = new(Agent.DefaultCompactionThreshold, 70.0, 40.0);
  private static readonly CompactionPolicy Medium = new(70.0, 60.0, 35.0);
  private static readonly CompactionPolicy Large = new(60.0, 50.0, 30.0);
  private static readonly CompactionPolicy VeryLarge = new(50.0, 40.0, 25.0);
}
