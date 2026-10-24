namespace eThangAgent.ModelDomain;

/// <summary>Accounting snapshot for a session: the provider-scored context size (last
///     request's input tokens - the truth), accumulated totals, and utilization against
///     the model's catalog window.
/// <para>The cache and reasoning members are provider-scored too and stay NULL (or
///     zero-total) when the provider reports nothing for them - never fabricated as
///     zero (issue #262). <see cref="LastCachedInputTokens"/> and
///     <see cref="LastReasoningTokens"/> describe the LAST request; the totals
///     accumulate over the session.</para>
/// <para><see cref="CacheHitPercent"/> is DERIVED here rather than carried, so a
///     caller can never hand in a ratio that disagrees with the two counts it comes
///     from.</para></summary>
public sealed record ContextStatus(
    int? LastInputTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    int? ContextWindow,
    double? UtilizationPercent,
    int? LastCachedInputTokens = null,
    long TotalCachedInputTokens = 0,
    int? LastReasoningTokens = null,
    long TotalReasoningTokens = 0)
{
  /// <summary>The prompt-cache hit rate of the last request as a percentage of its
  ///     input tokens, or null when the provider reported no cache count (or reported
  ///     a request with no input tokens). This is the number the reasoning-in-context
  ///     note calls the dominant cost lever.</summary>
  public double? CacheHitPercent => LastCachedInputTokens is { } cached && LastInputTokens is > 0
      ? Math.Round(cached * 100.0 / LastInputTokens.Value, 2)
      : null;
}
