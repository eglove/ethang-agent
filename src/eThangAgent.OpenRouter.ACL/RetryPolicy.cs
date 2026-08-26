namespace eThangAgent.OpenRouter.ACL;

/// <summary>
///     Retry policy for transient OpenRouter failures. Attempts are capped; the delay before
///     each retry doubles per attempt from <see cref="BaseDelay"/> (scaled by a jitter factor
///     in [0, 1], i.e. a 1x-2x spread) and is capped at <see cref="MaxDelay"/>. A longer
///     server-provided Retry-After hint wins over the computed backoff.
/// </summary>
public sealed record RetryPolicy
{
  private const int DefaultMaxAttempts = 4;
  private static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(500);
  private static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(8);

  public int MaxAttempts { get; }

  public TimeSpan BaseDelay { get; }

  public TimeSpan MaxDelay { get; }

  public RetryPolicy(int maxAttempts = DefaultMaxAttempts,
      TimeSpan? baseDelay = null,
      TimeSpan? maxDelay = null)
  {
    if (maxAttempts < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts,
          "MaxAttempts must be at least 1.");
    }

    TimeSpan effectiveBase = baseDelay ?? DefaultBaseDelay;
    if (effectiveBase <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(baseDelay), effectiveBase,
          "BaseDelay must be positive.");
    }

    TimeSpan effectiveMax = maxDelay ?? DefaultMaxDelay;
    if (effectiveMax < effectiveBase)
    {
      throw new ArgumentOutOfRangeException(nameof(maxDelay), effectiveMax,
          "MaxDelay must not be smaller than BaseDelay.");
    }

    MaxAttempts = maxAttempts;
    BaseDelay = effectiveBase;
    MaxDelay = effectiveMax;
  }

  public static RetryPolicy Default { get; } = new();

  /// <summary>Backoff before retrying attempt N (1-based): <see cref="BaseDelay"/> doubled per
  ///     prior attempt, scaled by (1 + <paramref name="jitter"/>), never below
  ///     <paramref name="retryAfter"/>, capped at <see cref="MaxDelay"/>.</summary>
  public TimeSpan ComputeDelay(int attempt, double jitter, TimeSpan? retryAfter)
  {
    if (attempt < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(attempt), attempt,
          "Attempt is 1-based and must be at least 1.");
    }

    if (jitter is < 0.0 or > 1.0)
    {
      throw new ArgumentOutOfRangeException(nameof(jitter), jitter,
          "Jitter must be within [0, 1].");
    }

    double scaled = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) * (1 + jitter);
    TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(scaled, MaxDelay.TotalMilliseconds));
    if (retryAfter is { } hint && hint > delay)
    {
      delay = TimeSpan.FromMilliseconds(Math.Min(hint.TotalMilliseconds, MaxDelay.TotalMilliseconds));
    }

    return delay;
  }
}
