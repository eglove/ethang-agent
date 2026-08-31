namespace eThangAgent.AgentDomain;

/// <summary>Decision the watchdog enacts for one agent record.</summary>
public enum WatchdogPolicyDecision
{
  Watch,
  RetryWrapUp,
  TerminalReport,
}

/// <summary>Pure watchdog decision policy: idle age plus retry attempts in, action out.
///     No I/O, no clock - the caller hands in computed facts, so the policy is fully
///     unit-testable and the enactment stays in the application layer. Validation is
///     unconditional: every construction path runs the same guards (strictness at the
///     boundary - no silent acceptance of an invalid attempts count).</summary>
public sealed record WatchdogPolicy
{
  public TimeSpan IdleThreshold { get; }
  public TimeSpan SettleWait { get; }
  public int MaxWrapUpAttempts { get; }

  public WatchdogPolicy(TimeSpan IdleThreshold, TimeSpan SettleWait, int MaxWrapUpAttempts)
  {
    if (IdleThreshold <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(IdleThreshold), "IdleThreshold must be positive.");
    }

    if (SettleWait <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(SettleWait), "SettleWait must be positive.");
    }

    if (MaxWrapUpAttempts < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(MaxWrapUpAttempts), "MaxWrapUpAttempts must not be negative.");
    }

    this.IdleThreshold = IdleThreshold;
    this.SettleWait = SettleWait;
    this.MaxWrapUpAttempts = MaxWrapUpAttempts;
  }

  public WatchdogPolicy()
      : this(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60), 1)
  {
  }

  public WatchdogPolicyDecision Decide(bool isChild, TimeSpan? idleAge, int wrapUpAttempts)
      => !isChild || idleAge is not { } age || age < IdleThreshold
          ? WatchdogPolicyDecision.Watch
          : AttemptDecision(wrapUpAttempts);

  private WatchdogPolicyDecision AttemptDecision(int wrapUpAttempts)
      => wrapUpAttempts >= MaxWrapUpAttempts ? WatchdogPolicyDecision.TerminalReport : WatchdogPolicyDecision.RetryWrapUp;

  /// <summary>Verbatim wrap-up prompt sent to a resumed child instead of its original
  ///     task prompt. The model continues its own conversation and owes a final report.</summary>
  public static string WrapUpNudge(int idleMinutes)
      => "[watchdog] You showed no activity for " + idleMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " minutes. Continue from where you stopped and wrap up now: finish any essential work and produce your final report as your next reply.";
}
