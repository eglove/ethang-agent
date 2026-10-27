namespace eThangAgent.ModelDomain;

/// <summary>The arithmetic the performance read model is built from (issue #295), as PURE
///     functions — no clock, no store, no ambient state — so the outlier rule, the
///     zero-output exclusion, and the hit-rate derivation are unit tested against fixtures
///     rather than inferred from a database.
///     <para>WHY ONE PLACE: <see cref="UsageCost"/> and <see cref="EffectivePrice"/> are the
///     precedent — one formula, one implementation, so no caller can invent a second version
///     that disagrees. A median computed twice, once in SQL and once in C#, is exactly that
///     second version.</para></summary>
public static class UsageStatistics
{
  /// <summary>The MEDIAN of the supplied measurements, or null when there are none (D2).
  ///     <para>MEDIAN, NOT MEAN, and the difference is the point: provider latency has a long
  ///     right tail — a single 578-second call measured in this workspace would move a mean by
  ///     seconds while telling the user nothing about the typical experience. The median of
  ///     that same set is unmoved.</para>
  ///     <para>An even count averages the two central values, which is the standard definition
  ///     and keeps the figure continuous as samples are added. This method does not sort its
  ///     input in place: the caller's list is copied first, because a measurement helper that
  ///     reordered its caller's data would be a surprise nobody asked for.</para></summary>
  public static double? Median(IEnumerable<double>? measurements)
  {
    if (measurements is null)
    {
      return null;
    }

    double[] sorted = [.. measurements.OrderBy(value => value)];
    if (sorted.Length == 0)
    {
      return null;
    }

    int middle = sorted.Length / 2;
    return sorted.Length % 2 == 1
        ? sorted[middle]
        : (sorted[middle - 1] + sorted[middle]) / 2d;
  }

  /// <summary>The value at the given percentile of the supplied measurements, or null when
  ///     there are none. Used for the reliability signal (issue #295): the p90 of a provider's
  ///     call durations is how long its slow calls take, which a median alone hides.
  ///     <para>NEAREST-RANK, NOT INTERPOLATED: with few samples an interpolated percentile
  ///     reports a duration that no call actually took, and the whole value of this figure is
  ///     that it is a measurement. The rank is clamped into range so a caller's percentile
  ///     outside (0,1] still answers with a real sample.</para></summary>
  /// <param name="measurements">The observed values.</param>
  /// <param name="percentile">The percentile to read, in (0,1].</param>
  public static double? Percentile(IEnumerable<double>? measurements, double percentile)
  {
    if (measurements is null)
    {
      return null;
    }

    if (percentile is <= 0d or > 1d)
    {
      throw new ArgumentOutOfRangeException(nameof(percentile), percentile,
          "A percentile must be in (0,1].");
    }

    double[] sorted = [.. measurements.OrderBy(value => value)];
    if (sorted.Length == 0)
    {
      return null;
    }

    int rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
  }

  /// <summary>One call's output rate in tokens per second, or null when the call cannot
  ///     express one (D3).
  ///     <para>EXCLUDED, NOT CLAMPED: a call that produced no output tokens (a tool-call-only
  ///     response) has no meaningful rate, and letting it through would drag the median toward
  ///     zero and describe a provider as slow when it simply had nothing to say. A call with no
  ///     measured duration has no rate either — that is the 'not measured' case, not a zero.
  ///     A zero duration with real output is a degenerate measurement (the clock's resolution
  ///     ran out), and dividing by it would report an infinite rate; it is excluded for the same
  ///     reason.</para></summary>
  public static double? OutputTokensPerSecond(int outputTokens, long? durationMs)
      => outputTokens > 0 && durationMs is { } duration && duration > 0
          ? outputTokens * 1000d / duration
          : null;

  /// <summary>The cache-hit share of the prompt tokens billed, or null when none were (D4).
  ///     <para>DERIVED, NEVER CARRIED, on the same rule <see cref="ContextStatus.CacheHitPercent"/>
  ///     follows: a ratio handed in separately can disagree with the counts it claims to
  ///     summarize, and this is the number a price projection multiplies by. A cached count
  ///     the provider never reported reads as zero hits rather than as unknown, because the
  ///     hit rate is a property of the tokens actually billed and an unreported cache count
  ///     means the provider scored none.</para>
  ///     <para>A hit rate is clamped into [0,1]: a provider that reported more cached tokens
  ///     than input tokens has reported something impossible, and a rate above 1 would make
  ///     <see cref="EffectivePrice"/> project a negative uncached share.</para></summary>
  public static decimal? HitRate(long cachedInputTokens, long inputTokens)
      => inputTokens <= 0
          ? null
          : Math.Clamp((decimal)cachedInputTokens / inputTokens, 0m, 1m);
}
