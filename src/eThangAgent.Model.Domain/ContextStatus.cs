namespace eThangAgent.ModelDomain;

/// <summary>Accounting snapshot for a session: the provider-scored context size (last
///     request's input tokens - the truth), accumulated totals, and utilization against
///     the model's catalog window.
/// <para><see cref="TotalCachedInputTokens"/> is the prompt-cache hit the provider reported,
///     summed over the session, and stays NULL when the provider has reported no cache
///     count at all - never fabricated as zero. A reported zero is a measured cold cache
///     and reads as a real zero.</para>
/// <para><see cref="CacheHitPercent"/> is DERIVED here rather than carried, so a
///     caller can never hand in a ratio that disagrees with the two counts it comes
///     from. It covers the WHOLE session rather than the last request: a single
///     request's rate swings with the size of that one prompt, while the number is read
///     to judge how much of the session's prompt spend the cache absorbed.</para></summary>
public sealed record ContextStatus(
    int? LastInputTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    int? ContextWindow,
    double? UtilizationPercent,
    long? TotalCachedInputTokens = null)
{
  /// <summary>The session's prompt-cache hit rate as a percentage of every prompt token
  ///     it was billed for, or null when the provider reported no cache count (or the
  ///     session has billed no prompt tokens). This is the number the reasoning-in-context
  ///     note calls the dominant cost lever.</summary>
  public double? CacheHitPercent => TotalCachedInputTokens is { } cached && TotalInputTokens > 0
      ? Math.Round(cached * 100.0 / TotalInputTokens, 2)
      : null;
}
