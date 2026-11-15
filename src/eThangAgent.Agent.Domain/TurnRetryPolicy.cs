namespace eThangAgent.AgentDomain;

/// <summary>The bounds on one turn's transient-fault retries: six attempts, delays
///     doubling from one second to a thirty-second cap, and a total sleep budget.
///     <para>THE TWO CODE SETS ARE THE DOMAIN'S ONLY CLASSIFICATION INPUT. A provider
///     failure carries a domain code, never an HTTP status, so what a turn does about a
///     failure is decided here from the code alone. A code in neither set
///     (<c>ContextWindowExceeded</c>, <c>TurnCancelled</c>, <c>HostUnavailable</c>, ...)
///     keeps its own handling.</para>
///     <para>PURE: attempt numbers and elapsed time in, decisions out. The waiting itself
///     belongs to the loop that owns the request.</para></summary>
public static class TurnRetryPolicy
{
  /// <summary>The retried codes: <c>StreamInterrupted</c>, <c>ProviderError</c>,
  ///     <c>ProviderTimeout</c>.</summary>
  private static readonly string[] TransientCodes =
  [
      "StreamInterrupted",
      "ProviderError",
      "ProviderTimeout",
  ];

  /// <summary>The codes that mean the model refused the request rather than the request
  ///     or the connection failing: <c>RateLimited</c>, <c>ModelRejected</c>.</summary>
  private static readonly string[] ModelVerdictCodes =
  [
      "RateLimited",
      "ModelRejected",
  ];

  /// <summary>Attempts one turn may spend on the same request, the first one included.</summary>
  public const int MaxAttempts = 6;

  /// <summary>The wait before the second attempt; each later wait doubles it.</summary>
  public static TimeSpan BaseDelay => TimeSpan.FromSeconds(1);

  /// <summary>The ceiling on any single wait.</summary>
  public static TimeSpan MaxDelay => TimeSpan.FromSeconds(30);

  /// <summary>The most the waits of one turn may add up to.</summary>
  public static TimeSpan TotalBudget => TimeSpan.FromSeconds(180);

  /// <summary>The production wait: a real delay that observes <paramref name="ct"/>.
  ///     The loop awaits the waiter its options carry, so a test substitutes a completed
  ///     task in place of this one and spends no clock time.</summary>
  public static Task SleepAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

  /// <summary>The retry notice: the attempt about to be made, its wait in whole seconds,
  ///     and the failed attempt's message.</summary>
  public static string Describe(int attempt, TimeSpan delay, string reason) =>
      $"[retry {attempt} of {MaxAttempts}] waiting {(int)Math.Ceiling(delay.TotalSeconds)}s: {reason}";

  /// <summary>True when <paramref name="code"/> is a transient fault worth retrying.
  ///     A model verdict is false here: the same request against the same model fails the
  ///     same way, so a retry spends the turn's attempts and buys nothing.</summary>
  public static bool IsTransient(string code) =>
      TransientCodes.Contains(code, StringComparer.Ordinal);

  /// <summary>True when <paramref name="code"/> is the model's verdict on the request or
  ///     on itself.</summary>
  public static bool IsModelVerdict(string code) =>
      ModelVerdictCodes.Contains(code, StringComparer.Ordinal);

  /// <summary>The wait before <paramref name="attempt"/>: <see cref="BaseDelay"/> doubled
  ///     per prior attempt, scaled by a factor in [1, 2] from <paramref name="jitter"/>, and
  ///     capped at <see cref="MaxDelay"/>.</summary>
  public static TimeSpan ComputeDelay(int attempt, double jitter)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0.0);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.0);

    double scaled = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (1 + jitter);
    return TimeSpan.FromMilliseconds(Math.Min(scaled, MaxDelay.TotalMilliseconds));
  }

  /// <summary>True when another attempt is allowed: the attempts already spent are below
  ///     <see cref="MaxAttempts"/>, and the waits so far plus the wait this attempt would
  ///     take stay within <see cref="TotalBudget"/>.</summary>
  public static bool AllowsRetry(int attempt, TimeSpan sleptSoFar, TimeSpan nextDelay)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
    return attempt < MaxAttempts && sleptSoFar + nextDelay <= TotalBudget;
  }
}
