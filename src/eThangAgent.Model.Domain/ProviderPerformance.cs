namespace eThangAgent.ModelDomain;

/// <summary>Which slice of the usage ledger answered a performance query (issue #295, D5).
///     <para>SCOPE IS EXPLICIT AND THE ANSWER NAMES IT: a fresh session has no rows of its own,
///     so the picker falls back to the workspace's history and then to the whole ledger — and a
///     projection built on a scope the caller was not told about is a guess wearing a decimal
///     point. <see cref="ProviderPerformanceReport.Scope"/> is therefore the scope that
///     ACTUALLY answered, never the one that was asked for first.</para></summary>
public enum UsageScope
{
  /// <summary>The session's own rows, walked down through every agent it spawned.</summary>
  Session,

  /// <summary>Every session recorded against one workspace.</summary>
  Workspace,

  /// <summary>Every row in the ledger, whatever workspace wrote it.</summary>
  Global,
}

/// <summary>What a caller wants performance measured over (issue #295, D5).
///     <para>THE FALLBACK CHAIN IS FIXED AND DOCUMENTED rather than caller-supplied: session,
///     then workspace, then global. A configurable chain would let two callers disagree about
///     which history a number came from while showing the same figure, and the whole point of
///     D5 is that the provenance is stated. A missing id simply skips its rung.</para></summary>
/// <param name="SessionId">The session to measure first, or null to skip that rung.</param>
/// <param name="WorkspaceId">The workspace to fall back to, or null to skip that rung.</param>
public sealed record ProviderPerformanceRequest(string? SessionId = null, string? WorkspaceId = null)
{
  /// <summary>A session's own history, falling back to its workspace and then the ledger.</summary>
  public static ProviderPerformanceRequest ForSession(string sessionId, string? workspaceId = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    return new(sessionId, workspaceId);
  }

  /// <summary>One workspace's history, falling back to the ledger.</summary>
  public static ProviderPerformanceRequest ForWorkspace(string workspaceId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    return new(null, workspaceId);
  }
}

/// <summary>What one model/provider pair has actually done in the ledger (issue #295): the
///     MEDIAN call duration, the MEDIAN first-token latency, the MEDIAN output rate, and the
///     token counts the pair's cache-hit rate derives from — each with the number of samples
///     it rests on.
///     <para>WHY THE MODEL IS PART OF THE KEY: latency is a property of the PAIR, not of the
///     provider alone — the same vendor serving a small model and a frontier model produces
///     two different numbers, and averaging them would describe neither. The key is exactly
///     the pair a provider comparison ranks (<see cref="RankableProvider"/>), so the read
///     model's answer can be handed to <see cref="ProviderRanking"/> without a second
///     aggregation.</para>
///     <para>EVERY MEASUREMENT IS NULLABLE AND NULL IS 'NOT MEASURED', never zero (the rule
///     #292 established for the row and #293 for the catalog). A zero-sample figure is null,
///     and a caller renders it as unavailable rather than as an instant, free, or infinitely
///     slow provider.</para>
///     <para>THE SAMPLE COUNT IS PART OF THE ANSWER (D1): a median over three calls
///     is not a median over three thousand, and a surface that showed them identically would
///     present a coincidence as a fact.</para></summary>
/// <param name="ModelId">The model whose calls these were, or null when the rows could not
///     name one.</param>
/// <param name="ProviderName">The serving provider when the wire named one, else the provider
///     whose prices were stamped, or null when the rows name neither.</param>
/// <param name="MedianDurationMs">The median whole-call duration in milliseconds, or null when
///     no call in scope was timed.</param>
/// <param name="P90DurationMs">The 90th-percentile whole-call duration, or null when no call in
///     scope was timed. This is the ledger's RELIABILITY signal and it is deliberately
///     uptime-INDEPENDENT: the catalog's uptime is a served-request success rate for an endpoint
///     we may never reach, while the gap between this pair's median and its p90 is how often the
///     provider we DID reach made us wait. See <see cref="SlowTailRatio"/>.</param>
/// <param name="MedianFirstTokenMs">The median time to the first streamed delta, or null when
///     no call in scope streamed one.</param>
/// <param name="MedianOutputTokensPerSecond">The median per-call output rate, or null when no
///     call in scope produced output in a measurable time (D3).</param>
/// <param name="LatencySamples">How many calls the duration median rests on.</param>
/// <param name="FirstTokenSamples">How many calls the first-token median rests on.</param>
/// <param name="ThroughputSamples">How many calls the throughput median rests on.</param>
/// <param name="UnmeasuredCalls">How many calls were EXCLUDED from latency because nothing
///     timed them (B6) — counted, never treated as zero.</param>
/// <param name="Calls">Every call in the group, measured or not.</param>
/// <param name="InputTokens">Prompt tokens the group was billed for, cached share included.</param>
/// <param name="CachedInputTokens">The prompt-cache hits the group reported.</param>
public sealed record ProviderPerformance(
    string? ModelId,
    string? ProviderName,
    double? MedianDurationMs,
    double? P90DurationMs,
    double? MedianFirstTokenMs,
    double? MedianOutputTokensPerSecond,
    int LatencySamples,
    int FirstTokenSamples,
    int ThroughputSamples,
    int UnmeasuredCalls,
    int Calls,
    long InputTokens,
    long CachedInputTokens)
{
  /// <summary>This pair's measured cache-hit rate, or null when it was billed for no input
  ///     tokens (D4). Derived, never carried, so it cannot disagree with the counts it came
  ///     from.</summary>
  public decimal? HitRate => UsageStatistics.HitRate(CachedInputTokens, InputTokens);

  /// <summary>True when this pair's calls were actually timed — the difference between
  ///     'fast' and 'nobody measured it'.</summary>
  public bool HasLatency => MedianDurationMs.HasValue;

  /// <summary>True when this pair's output rate could be computed at all.</summary>
  public bool HasThroughput => MedianOutputTokensPerSecond.HasValue;

  /// <summary>How many times slower the tail is than the typical call, or null when either end
  ///     is unmeasured. 1.0 means a provider whose slow calls are no slower than its median;
  ///     8.0 means one that occasionally takes eight times as long, which is the difference
  ///     between a pair that feels steady and one that stalls.
  ///     <para>WHY A RATIO AND NOT A SCORE: it is a property of the provider's own distribution,
  ///     so no threshold, weight, or invented constant enters it — the same discipline
  ///     <see cref="ProviderRanking"/> follows by sorting on dimensions instead of a composite.
  ///     </para></summary>
  public double? SlowTailRatio => MedianDurationMs is { } median && median > 0d
      && P90DurationMs is { } tail
          ? tail / median
          : null;
}

/// <summary>What the ledger says about every model/provider pair in one scope (issue #295),
///     plus the scope-wide cache-hit rate the picker projects prices at (D4).
///     <para>THE HIT RATE IS A FIRST-CLASS OUTPUT OF THE SAME QUERY, not a second lookup: the
///     number the picker multiplies prices by and the number the status bar rolls up must be
///     the same number, and two queries over two row sets can disagree. It is derived from the
///     same accumulator the status bar uses (<see cref="UsageTally"/>), so the agreement is
///     structural rather than a coincidence two implementations maintain.</para>
///     <para><see cref="Truncated"/> states that the read stopped at the row cap, so the
///     figures rest on the newest rows rather than on all history. A truncated median is still
///     a real median of what it read; what it must never do is present itself as covering
///     everything.</para></summary>
/// <param name="Scope">The scope that ANSWERED, which may be a fallback rung (D5).</param>
/// <param name="HitRate">The scope-wide cache-hit share, or null when no input tokens were
///     billed.</param>
/// <param name="InputTokens">Prompt tokens in scope, cached share included.</param>
/// <param name="CachedInputTokens">Prompt-cache hits in scope.</param>
/// <param name="Calls">Every call in scope.</param>
/// <param name="UnmeasuredCalls">Calls in scope that nothing timed.</param>
/// <param name="Providers">One row per model/provider pair, in a deterministic order.</param>
/// <param name="Truncated">True when the read reached its row cap.</param>
public sealed record ProviderPerformanceReport(
    UsageScope Scope,
    decimal? HitRate,
    long InputTokens,
    long CachedInputTokens,
    int Calls,
    int UnmeasuredCalls,
    IReadOnlyList<ProviderPerformance> Providers,
    bool Truncated = false)
{
  /// <summary>The report for a scope with no rows at all: an ANSWER, not a failure — a
  ///     first-ever session has measured nothing, and a surface renders that state.</summary>
  public static ProviderPerformanceReport Empty(UsageScope scope) =>
      new(scope, null, 0, 0, 0, 0, []);

  /// <summary>True when any call was counted.</summary>
  public bool HasRows => Calls > 0;

  /// <summary>The measurements for one model/provider pair, or null when that pair never ran
  ///     in scope. The pair is the ranking key, so this is the lookup a provider comparison
  ///     makes (<see cref="RankableProvider"/>).</summary>
  public ProviderPerformance? Lookup(string? modelId, string? providerName) =>
      Providers.FirstOrDefault(row => row.ModelId == modelId && row.ProviderName == providerName);

  /// <summary>These measurements paired onto catalog rows, ready for
  ///     <see cref="ProviderRanking.Rank(IReadOnlyList{RankableProvider}, RankingCriteria)"/>
  ///     (issue #295's whole purpose: it is the seam the picker ranks through).
  ///     <para>MATCHING IS BY THE PAIR, and a pair with no measurement gets NULLS rather than
  ///     zeros — ranking treats an unmeasured provider as unmeasured and sorts it last, which
  ///     is the honest order. A catalog row whose provider is unnamed matches this report's
  ///     unnamed bucket only by exact equality, so a fabricated name can never silently bind
  ///     one provider's measurements to another's row.</para>
  ///     <para>The OBSERVED LATENCY is the median duration, not the p90: ranking prefers a
  ///     typical call, and the tail is a separate dimension the caller can show beside it.
  ///     </para></summary>
  public IReadOnlyList<RankableProvider> Rankable(IReadOnlyList<ModelProviderEntry> entries)
  {
    ArgumentNullException.ThrowIfNull(entries);
    return
    [
        .. entries.Select(entry =>
        {
          ProviderPerformance? measured = Lookup(entry.ModelId, entry.ProviderName);
          return new RankableProvider(entry, measured?.MedianDurationMs,
              measured?.MedianOutputTokensPerSecond);
        }),
    ];
  }
}
