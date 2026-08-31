namespace eThangAgent.Agent.Application;

/// <summary>Validated watchdog configuration. Strictness at the boundary: invalid values
///     throw at construction, never clamp. Hosts pass this at the composition root; the
///     property names here are the contract every consumer uses.</summary>
public sealed class WatchdogOptions(
  bool Enabled = true,
  TimeSpan? TickInterval = null,
  TimeSpan? IdleThreshold = null,
  TimeSpan? SettleWait = null,
  int MaxWrapUpAttempts = 1,
  double RssThresholdMb = 4096,
  TimeSpan? RssReReportInterval = null)
{
  public static WatchdogOptions Default { get; } = new();

  public bool Enabled { get; } = Enabled;
  public TimeSpan TickInterval { get; } = Positive(TickInterval ?? TimeSpan.FromSeconds(60), nameof(TickInterval));
  public TimeSpan IdleThreshold { get; } = Positive(IdleThreshold ?? TimeSpan.FromMinutes(15), nameof(IdleThreshold));
  public TimeSpan SettleWait { get; } = Positive(SettleWait ?? TimeSpan.FromSeconds(60), nameof(SettleWait));
  public int MaxWrapUpAttempts { get; } = MaxWrapUpAttempts >= 0
      ? MaxWrapUpAttempts
      : throw new ArgumentOutOfRangeException(nameof(MaxWrapUpAttempts), "MaxWrapUpAttempts must not be negative.");
  public double RssThresholdMb { get; } = RssThresholdMb > 0
      ? RssThresholdMb
      : throw new ArgumentOutOfRangeException(nameof(RssThresholdMb), "RssThresholdMb must be positive.");
  public TimeSpan RssReReportInterval { get; } = Positive(RssReReportInterval ?? TimeSpan.FromMinutes(10), nameof(RssReReportInterval));

  private static TimeSpan Positive(TimeSpan value, string paramName) => value > TimeSpan.Zero
      ? value
      : throw new ArgumentOutOfRangeException(paramName, "must be positive.");
}
