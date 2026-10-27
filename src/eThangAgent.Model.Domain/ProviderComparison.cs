using System.Globalization;

namespace eThangAgent.ModelDomain;

/// <summary>The four orderings the picker offers (issue #296, D4), each a list of
///     <see cref="ProviderPriority"/> rather than a weight.
///     <para>PRESETS REORDER; THEY NEVER HIDE (D4). Every preset is a priority list and no
///     preset carries a <see cref="ProviderFilters"/>, because a user who picks 'cheapest'
///     still needs to see that the cheapest option has 87% uptime. A preset that filtered
///     would answer a question the user did not ask.</para>
///     <para>THE PRIORITY LISTS ARE LONG ENOUGH TO BE USEFUL AND SHORT ENOUGH TO EXPLAIN:
///     the named dimension decides, and the remaining priorities are ordinary tiebreak
///     dimensions in a documented order - never a composite score (the parent's D4).</para>
///     </summary>
public enum ProviderPreset
{
  /// <summary>Cheapest blended input price first.</summary>
  Cheapest,

  /// <summary>Shortest observed call first.</summary>
  Fastest,

  /// <summary>Highest served-request success rate first.</summary>
  MostReliable,

  /// <summary>Highest coding score first, then intelligence, then agentic.</summary>
  BestCapability,
}

/// <summary>The preset to priority translation (issue #296, D4), as a PURE function so the
///     surface cannot invent a second ordering that disagrees with the one it displays.
///     <para>EVERY PRESET ENDS IN THE SAME DETERMINISTIC TAIL: price, then latency, then
///     reliability. Two rows that tie on the named dimension therefore order the same way
///     under every preset, which is what stops the list reshuffling when the user toggles
///     between presets that happen to agree.</para></summary>
public static class ProviderPresets
{
  /// <summary>The priorities one preset ranks by, best first.</summary>
  public static IReadOnlyList<ProviderPriority> Priorities(ProviderPreset preset) => preset switch
  {
    ProviderPreset.Cheapest =>
    [
      ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice),
      ProviderPriority.Ascending(ProviderDimension.Latency),
      ProviderPriority.DescendingOrder(ProviderDimension.Reliability),
    ],
    ProviderPreset.Fastest =>
    [
      ProviderPriority.Ascending(ProviderDimension.Latency),
      ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice),
      ProviderPriority.DescendingOrder(ProviderDimension.Reliability),
    ],
    ProviderPreset.MostReliable =>
    [
      ProviderPriority.DescendingOrder(ProviderDimension.Reliability),
      ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice),
      ProviderPriority.Ascending(ProviderDimension.Latency),
    ],
    ProviderPreset.BestCapability =>
    [
      ProviderPriority.DescendingOrder(ProviderDimension.Coding),
      ProviderPriority.DescendingOrder(ProviderDimension.Intelligence),
      ProviderPriority.DescendingOrder(ProviderDimension.Agentic),
      ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice),
    ],
    _ => [ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice)],
  };

  /// <summary>The dimension the preset is 'about', which the surface highlights (D4: the
  ///     dimension that drove the order must be visible).</summary>
  public static ProviderDimension Highlighted(ProviderPreset preset) => preset switch
  {
    ProviderPreset.Cheapest => ProviderDimension.EffectiveInputPrice,
    ProviderPreset.Fastest => ProviderDimension.Latency,
    ProviderPreset.MostReliable => ProviderDimension.Reliability,
    ProviderPreset.BestCapability => ProviderDimension.Coding,
    _ => ProviderDimension.EffectiveInputPrice,
  };


  /// <summary>The preset's display name.</summary>
  public static string DisplayName(ProviderPreset preset) => preset switch
  {
    ProviderPreset.Cheapest => "Cheapest",
    ProviderPreset.Fastest => "Fastest",
    ProviderPreset.MostReliable => "Most reliable",
    ProviderPreset.BestCapability => "Best capability",
    _ => preset.ToString(),
  };
}

/// <summary>One provider row of the comparison, with every evidence figure already RENDERED
///     (issue #296, D2/D3/D5).
///     <para>WHY THE TEXT LIVES IN THE DOMAIN: D5's rule - an unavailable value is rendered
///     as unavailable, never as a zero - is a fact about honesty, not about layout. Putting
///     the strings here means one place decides that a missing latency is
///     <c>not measured</c> (through <see cref="ProviderPerformanceText"/>) and a null price is
///     <c>pricing unknown</c>, so no view can quietly print a zero instead. The view chooses
///     which of these strings are columns, which are detail-pane facts, and which drive a
///     sort control (D3 leaves that to the design pass); it never authors the figures.</para>
///     <para>THE DISPLAYED LATENCY IS THE RANKED LATENCY. <see cref="RankedProvider.LatencyMs"/>
///     prefers our own measurement and falls back to the catalog's advertised figure, and it
///     is the value the order was computed from - showing anything else would be the same
///     class of defect as the cheapest-endpoint bug this surface replaces.</para></summary>
/// <param name="Ranked">The ranked row: the catalog entry, its effective price, and the
///     observations that were available for it.</param>
/// <param name="Measured">The ledger's measurements for this pair, or null when the pair
///     never ran in scope. Null is NOT zero (D5): it is what makes the latency cell read
///     <c>not measured</c>.</param>
/// <param name="Assumption">The hit rate the effective price was computed at, carried so the
///     row can never be read without it.</param>
public sealed record ProviderEvidenceRow(
    RankedProvider Ranked,
    ProviderPerformance? Measured,
    HitRateAssumption Assumption)
{
  /// <summary>The provider name, or a marker when the catalog named none. A row with no
  ///     provider is still a row: it is the model's own default endpoint.</summary>
  public string ProviderName => Ranked.Entry.ProviderName ?? UnnamedProvider;

  /// <summary>What an unnamed provider renders as. Spelled out rather than left blank,
  ///     because a blank cell reads as an oversight (the same rule
  ///     <see cref="ProviderPerformanceText.NotMeasured"/> follows).</summary>
  public const string UnnamedProvider = "(unnamed)";

  /// <summary>What a surface says when the catalog advertised no price. Never <c>$0</c>:
  ///     an unknown price is the opposite of a known price of zero (issue #289).</summary>
  public const string PricingUnknown = "pricing unknown";

  /// <summary>What a surface says for a provider that states it does not cache implicitly.
  ///     The advertised cache-read price is then not a price any token will be charged
  ///     (D3 of issue #294).</summary>
  public const string NoCacheSupport = "no cache support";

  /// <summary>What a surface says when the catalog never stated whether the provider caches.
  ///     DELIBERATELY DISTINCT FROM <see cref="NoCacheSupport"/>: a row persisted before the
  ///     field existed has said nothing, and telling the user a caching provider does not
  ///     cache is the exact misstatement the tri-state exists to prevent.</summary>
  public const string CacheSupportUnverified = "cache support unverified";

  /// <summary>The primary figure (D2): the blended input price per million tokens at the
  ///     stated hit rate, or <see cref="PricingUnknown"/> when a price it needs is unknown.
  ///     </summary>
  public string EffectivePriceText => Ranked.EffectivePrice.ReadPerInputToken is { } perToken
      ? FormatPerMillion(perToken) + "/M effective"
      : PricingUnknown;

  /// <summary>The secondary figure (D2): the advertised prompt price, which is what the user
  ///     recognizes. Rendered from the same price-ladder rung the effective price used, so the
  ///     two figures describe the same call shape.</summary>
  public string PromptPriceText => Ranked.Entry.PromptPricePerToken is >= 0m and { } perToken
      ? FormatPerMillion(perToken) + "/M prompt"
      : PricingUnknown;

  /// <summary>The cache-discount cell, keyed on WHY the discount was or was not applied
  ///     (<see cref="CacheDiscountBasis"/>) rather than on the price alone (D5).
  ///     <para>THE THREE ABSENCES ARE DISTINCT ON PURPOSE: <c>no cache support</c> is a
  ///     provider that said it does not cache, <c>cache support unverified</c> is one that
  ///     said nothing, and <c>pricing unknown</c> is one whose prices nobody advertised.
  ///     Collapsing them would tell the user something the catalog never said.</para></summary>
  public string CacheDiscountText => Ranked.EffectivePrice.DiscountBasis switch
  {
    CacheDiscountBasis.ImplicitCachingSupported =>
        ProviderPerformanceText.CacheDiscount(Ranked.Entry.PromptPricePerToken,
            Ranked.Entry.PromptCacheReadPricePerToken),
    CacheDiscountBasis.ImplicitCachingUnsupported => NoCacheSupport,
    CacheDiscountBasis.ImplicitCachingUnverified => CacheSupportUnverified,
    CacheDiscountBasis.PricesUnknown => PricingUnknown,
    _ => PricingUnknown,
  };

  /// <summary>The latency cell: our own median call duration when the ledger measured one,
  ///     else the catalog's advertised figure, else <c>not measured</c> (D5, B3). A real zero
  ///     stays a real measurement - a call that finished inside a millisecond is measured, not
  ///     absent.</summary>
  public string LatencyText => ProviderPerformanceText.Latency(Ranked.LatencyMs);

  /// <summary>The throughput cell, on the same rule as <see cref="LatencyText"/>.</summary>
  public string ThroughputText => ProviderPerformanceText.Throughput(Ranked.ThroughputTokensPerSec);

  /// <summary>The reliability cell: the endpoint's served-request success rate, or
  ///     <c>not measured</c>.</summary>
  public string UptimeText => ProviderPerformanceText.Uptime(Ranked.Entry.UptimeLast30m);

  /// <summary>The context-window cell.</summary>
  public string ContextText => Ranked.Entry.ContextLength >= 1_000_000
      ? (Ranked.Entry.ContextLength / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M ctx"
      : (Ranked.Entry.ContextLength / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "K ctx";

  /// <summary>The capability cell: the catalog's coding, intelligence, and agentic scores,
  ///     each rendered or marked unmeasured. The three stay SEPARATE (the parent's D4 forbids
  ///     averaging them into one score), so they are three figures in one cell rather than a
  ///     composite.</summary>
  public string CapabilityText => $"coding {Score(Ranked.Entry.CodingScore)} · "
      + $"intelligence {Score(Ranked.Entry.IntelligenceScore)} · "
      + $"agentic {Score(Ranked.Entry.AgenticScore)}";

  /// <summary>How much evidence the latency figure rests on (D1 of issue #295: a median over
  ///     three calls is not a median over three thousand). Empty when nothing was measured,
  ///     because 'no samples' beside 'not measured' says the same thing twice.</summary>
  public string SampleText => Measured is { LatencySamples: > 0 } measured
      ? $"{measured.LatencySamples} timed call(s)"
      : string.Empty;

  /// <summary>The cache-hit rate this row's effective price was projected at, spelled out
  ///     with its provenance (issue #296 follow-up): <c>session measured 97.7%</c>,
  ///     <c>OpenRouter published 62.9%</c>, or <c>default 50% (nothing measured yet)</c>.
  ///     <para>PER ROW, NOT PER LIST: the endpoints serving one model differ hugely, so a
  ///     single caption above the list would describe none of them. Showing the rate beside
  ///     the price it produced is what makes the number checkable.</para></summary>
  public string HitRateText => Assumption.Describe();

  /// <summary>The one-line detail a list row shows: the primary figure first, then the
  ///     secondary prompt price, then the cache position (D2's shape).</summary>
  public string Detail => $"{EffectivePriceText} · {PromptPriceText} · {CacheDiscountText}";

  /// <summary>The evidence line a detail pane shows: every dimension D3 names, each rendered
  ///     or explicitly unavailable, plus the hit rate the price was projected at.</summary>
  public string Evidence => $"{Detail} · {HitRateText} · {LatencyText} · {ThroughputText} · {UptimeText} · {ContextText}";

  /// <summary>One catalog score, or <c>not measured</c> - never a zero (D5).</summary>
  private static string Score(double? score) => score is { } value
      ? value.ToString("0.#", CultureInfo.InvariantCulture)
      : ProviderPerformanceText.NotMeasured;

  /// <summary>A per-token price as a per-million figure. Up to four decimals below a dollar,
  ///     two above: an effective price is routinely a fraction of a cent ($0.0047/M), and
  ///     rounding it to two decimals would erase the difference the surface exists to show -
  ///     the same reason the status bar's cost keeps four decimals under a dollar. Invariant
  ///     culture, because this is a readout and not a locale-formatted amount.</summary>
  private static string FormatPerMillion(decimal perToken)
  {
    decimal perMillion = perToken * 1_000_000m;
    return perMillion < 1m
        ? "$" + perMillion.ToString("0.####", CultureInfo.InvariantCulture)
        : "$" + perMillion.ToString("0.##", CultureInfo.InvariantCulture);
  }
}

/// <summary>Builds the provider comparison the picker renders (issue #296, D1-D5) as a PURE
///     function over a catalog, a performance report, and a stated assumption.
///     <para>THERE IS EXACTLY ONE SORT: the rows come from
///     <see cref="ProviderRanking.Rank(IReadOnlyList{RankableProvider}, RankingCriteria)"/>
///     fed by <see cref="ProviderPerformanceReport.Rankable"/>, so the order the user sees is
///     the order the domain computed - not a second ordering that happens to agree
///     today.</para>
///     <para>NOTHING IS FILTERED (D4): the criteria carry no
///     <see cref="ProviderFilters"/>, because a preset that hid a row would answer a question
///     the user did not ask. A caller that genuinely wants a hard requirement supplies
///     filters explicitly, and then an empty result is the honest answer.</para></summary>
public static class ProviderComparison
{
  /// <summary>The comparison rows for one model's endpoints, best first under
  ///     <paramref name="preset"/>.
  ///     <para><paramref name="entries"/> is expected to be ONE model's provider rows (D1's
  ///     second level). Passing several models still works - the ranking's deterministic
  ///     tiebreak orders them - but the surface's model list is what separates the levels.</para>
  ///     </summary>
  /// <param name="entries">The catalog rows to compare.</param>
  /// <param name="report">The ledger's measurements, or null when the read model is
  ///     unavailable (headless hosts, a failed read). Null renders every measurement as
  ///     unavailable rather than failing the surface: a picker with no history is still a
  ///     picker.</param>
  /// <param name="rates">The per-pair hit rates each row is projected at (issue #296
  ///     follow-up). Required rather than defaulted: one rate for a list whose endpoints
  ///     range from 0% to 74% is wrong for nearly every row on it, so the caller must state
  ///     what each row is priced at.</param>
  /// <param name="preset">The ordering the user selected.</param>
  /// <param name="promptTokens">The prompt size the comparison is for, which selects the
  ///     applicable price-ladder rung. Zero selects the base prices.</param>
  public static IReadOnlyList<ProviderEvidenceRow> Rows(
      IReadOnlyList<ModelProviderEntry> entries,
      ProviderPerformanceReport? report,
      HitRateLookup rates,
      ProviderPreset preset,
      int promptTokens = 0)
  {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(rates);
    RankingCriteria criteria = new(ProviderPresets.Priorities(preset), rates.Fallback.Rate,
        promptTokens, null, rates);
    IReadOnlyList<RankableProvider> rankable = report is null
        ? [.. entries.Select(RankableProvider.Of)]
        : report.Rankable(entries);
    return
    [
        .. ProviderRanking.Rank(rankable, criteria).Select(ranked =>
            new ProviderEvidenceRow(ranked,
                report?.Lookup(ranked.Entry.ModelId, ranked.Entry.ProviderName),
                ranked.Assumption ?? rates.Fallback)),
    ];
  }
}
