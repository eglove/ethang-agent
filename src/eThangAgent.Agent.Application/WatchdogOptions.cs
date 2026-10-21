namespace eThangAgent.Agent.Application;

/// <summary>The RSS-watchdog knobs, grouped: the working-set threshold in MB, how long a
///     breach report stays rate-limited, and how many consecutive breach ticks mark a
///     breach sustained. Defaults match the flat constructor they replace; invalid values
///     throw at construction, never clamp.</summary>
public sealed record RssOptions
{
  public double ThresholdMb { get; }
  public TimeSpan ReReportInterval { get; }
  public int SustainedBreachTicks { get; }

  public RssOptions(double ThresholdMb = 4096, TimeSpan? ReReportInterval = null, int SustainedBreachTicks = 5)
  {
    this.ThresholdMb = ThresholdMb > 0
        ? ThresholdMb
        : throw new ArgumentOutOfRangeException(nameof(ThresholdMb), "RssThresholdMb must be positive.");
    this.ReReportInterval = ReReportInterval is { } interval
        ? Positive(interval)
        : TimeSpan.FromMinutes(10);
    this.SustainedBreachTicks = SustainedBreachTicks >= 0
        ? SustainedBreachTicks
        : throw new ArgumentOutOfRangeException(nameof(SustainedBreachTicks), "RssSustainedBreachTicks must not be negative.");
  }

  private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero
      ? value
      : throw new ArgumentOutOfRangeException(nameof(value), "must be positive.");
}

/// <summary>Validated watchdog configuration. Strictness at the boundary: invalid values
///     throw at construction, never clamp. Hosts pass this at the composition root; the
///     property names here are the contract every consumer uses.</summary>
public sealed class WatchdogOptions(
  bool Enabled = true,
  TimeSpan? TickInterval = null,
  TimeSpan? IdleThreshold = null,
  TimeSpan? SettleWait = null,
  int MaxWrapUpAttempts = 1,
  RssOptions? Rss = null,
  TimeSpan? ResourceTickInterval = null)
{
  public static WatchdogOptions Default { get; } = new();

  public bool Enabled { get; } = Enabled;
  public TimeSpan TickInterval { get; } = Positive(TickInterval ?? TimeSpan.FromSeconds(60), nameof(TickInterval));
  public TimeSpan IdleThreshold { get; } = Positive(IdleThreshold ?? TimeSpan.FromMinutes(15), nameof(IdleThreshold));
  public TimeSpan SettleWait { get; } = Positive(SettleWait ?? TimeSpan.FromSeconds(60), nameof(SettleWait));
  public int MaxWrapUpAttempts { get; } = MaxWrapUpAttempts >= 0
      ? MaxWrapUpAttempts
      : throw new ArgumentOutOfRangeException(nameof(MaxWrapUpAttempts), "MaxWrapUpAttempts must not be negative.");
  public double RssThresholdMb { get; } = Rss?.ThresholdMb ?? 4096;
  public TimeSpan RssReReportInterval { get; } = Rss?.ReReportInterval ?? TimeSpan.FromMinutes(10);
  public int RssSustainedBreachTicks { get; } = Rss?.SustainedBreachTicks ?? 5;

  /// <summary>The resource governor's own fast clock (spec D4) - deliberately separate
  ///     from <see cref="TickInterval"/>: the watchdog asks whether a sub-agent is stuck
  ///     once a minute, the governor asks whether the machine is filling up every couple
  ///     of seconds.</summary>
  public TimeSpan ResourceTickInterval { get; } = Positive(ResourceTickInterval ?? TimeSpan.FromSeconds(2), nameof(ResourceTickInterval));
  /// <summary>The resource governor's own fast clock (spec D4) - deliberately separate
  ///     from <see cref="TickInterval"/>: the watchdog asks whether a sub-agent is stuck
  ///     once a minute, the governor asks whether the machine is filling up every couple
  ///     of seconds.</summary>

  private static TimeSpan Positive(TimeSpan value, string paramName) => value > TimeSpan.Zero
      ? value
      : throw new ArgumentOutOfRangeException(paramName, "must be positive.");
}
