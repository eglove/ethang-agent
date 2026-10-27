namespace eThangAgent.ModelDomain.Tests;

// B4-B6 (issue #294): ordering provider rows by a caller's stated priorities. The rule is
// LEXICOGRAPHIC over named dimensions - no composite score is computed anywhere - with hard
// filters applied first and nulls last in both directions.
public class ProviderRankingTests
{
  private const decimal HitRate = 0.9m;

  /// <summary>One fixture row: a plain, cache-capable, tool-capable text model at a 128K
  ///     window with no measurements. Tests vary the facts they are about through the
  ///     helpers below or a <c>with</c> expression, so the row under test reads as the row
  ///     under test rather than as a wide argument list.</summary>
  private static ModelProviderEntry Entry(string provider, decimal prompt = 0.0000001m,
      decimal? cacheRead = 0.00000001m)
      => new("test/model", provider, prompt, cacheRead, 0.0000005m, 128_000, 8_192,
          SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: null, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
          SupportsImplicitCaching: true);

  /// <summary>The row with the latency the catalog advertises - the fact a latency test
  ///     varies, and null on every live endpoint (issue #293).</summary>
  private static ModelProviderEntry WithLatency(string provider, double? latencyMs)
      => Entry(provider) with { LatencyMs = latencyMs };

  /// <summary>The row with a reliability figure: the one performance signal that is
  ///     actually populated on the live catalog.</summary>
  private static ModelProviderEntry WithUptime(string provider, double? uptimePercent)
      => Entry(provider) with { UptimeLast30m = uptimePercent };

  /// <summary>The row with a context window.</summary>
  private static ModelProviderEntry WithContext(string provider, int contextLength)
      => Entry(provider) with { ContextLength = contextLength };

  /// <summary>Criteria with one priority and no filters - the shape most tests need.</summary>
  private static RankingCriteria By(ProviderDimension dimension, bool descending = false,
      ProviderFilters? filters = null)
      => new([new ProviderPriority(dimension, descending)], HitRate, Filters: filters);

  [Fact]
  public void Rank_OnePriority_OrdersByThatDimensionOnly()
  {
    // B4: the order is the priority's order. Nothing else about the rows is consulted.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        Entry("Expensive", prompt: 0.0000003m),
        Entry("Cheapest", prompt: 0.00000004m),
        Entry("Middling", prompt: 0.0000001m),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice));

    Assert.Equal(["Cheapest", "Middling", "Expensive"],
        ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_SecondPriorityDecidesOnlyAmongRowsTheFirstTied()
  {
    // B4's lexicographic half, and the reason no composite score is honest: a weighted sum
    // would let a large latency difference outvote a price the user ranked first. Here the
    // two cheap providers tie on price, so latency decides between them - and the expensive
    // but instant provider cannot overtake either of them.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithLatency("ExpensiveButFast", 100) with { PromptPricePerToken = 0.0000003m },
        WithLatency("CheapButSlow", 900) with { PromptPricePerToken = 0.00000004m },
        WithLatency("CheapAndFast", 300) with { PromptPricePerToken = 0.00000004m },
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        new RankingCriteria(
        [
            ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice),
            ProviderPriority.Ascending(ProviderDimension.Latency),
        ], HitRate));

    Assert.Equal(["CheapAndFast", "CheapButSlow", "ExpensiveButFast"],
        ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_DescendingPriority_PutsTheLargestFirst()
  {
    // Direction is a direction, not a weight: descending reliability simply reverses this
    // dimension's comparison.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithUptime("Flaky", 87.0),
        WithUptime("Solid", 99.9),
        WithUptime("Good", 95.0),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.Reliability, descending: true));

    Assert.Equal(["Solid", "Good", "Flaky"],
        ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_AscendingByLatency_NullLatencySortsLast()
  {
    // B5 / D5 ascending: an unmeasured latency is not a fast latency.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithLatency("Unmeasured", null),
        WithLatency("Slow", 900),
        WithLatency("Fast", 300),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.Latency));

    Assert.Equal(["Fast", "Slow", "Unmeasured"],
        ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_DescendingByLatency_NullLatencyStillSortsLast()
  {
    // B5 / D5 descending - THE HALF THAT IS EASY TO GET WRONG. Flipping the whole comparison
    // would float the null to the top and rank the provider nobody measured as the fastest.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithLatency("Unmeasured", null),
        WithLatency("Slow", 900),
        WithLatency("Fast", 300),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.Latency, descending: true));

    Assert.Equal(["Slow", "Fast", "Unmeasured"],
        ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_MaxPriceFilter_ExcludesRowsAboveIt()
  {
    // B6 / D6: a filter EXCLUDES, it does not rank low. The excluded provider is absent from
    // the result entirely, so no row in it violates the stated requirement.
    //
    // The ceiling applies to the EFFECTIVE price, not the prompt price: at a 90% hit rate
    // 'Pricey' costs 0.1*0.0000003 + 0.9*0.0000003 = 0.0000003 per input token, well above the
    // 0.0000001 ceiling, while 'Cheap' costs 0.1*0.00000004 + 0.9*0.00000001 = 0.000000013.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        Entry("Cheap", prompt: 0.00000004m),
        Entry("Pricey", prompt: 0.0000003m, cacheRead: 0.0000003m),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice,
            filters: new ProviderFilters(MaxEffectiveInputPricePerToken: 0.0000001m)));

    Assert.Equal(["Cheap"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_MaxPriceFilter_ExcludesRowsWhosePriceIsUnknown()
  {
    // An unknown price cannot be shown to be within the ceiling, so it is excluded rather
    // than presented as if it satisfied the requirement.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        Entry("Cheap", prompt: 0.00000004m),
        Entry("Unpriced", prompt: 0.00000004m, cacheRead: null),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice,
            filters: new ProviderFilters(MaxEffectiveInputPricePerToken: 0.0000001m)));

    Assert.Equal(["Cheap"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_UptimeFilter_ExcludesFlakyAndUnmeasuredProviders()
  {
    // The reliability floor has the same rule: 87% is below it, and a provider the catalog
    // never measured cannot be shown to clear it.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithUptime("Solid", 99.9),
        WithUptime("Flaky", 87.0),
        WithUptime("Unmeasured", null),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.Reliability,
            filters: new ProviderFilters(MinUptimePercent: 95.0)));

    Assert.Equal(["Solid"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_ContextLengthFilter_ExcludesNarrowWindows()
  {
    // 'Must have at least a 1M context window' is the parent issue's own example of a filter
    // that is not a preference.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        WithContext("Wide", 1_048_576),
        WithContext("Narrow", 128_000),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice,
            filters: new ProviderFilters(MinContextLength: 1_000_000)));

    Assert.Equal(["Wide"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_NoFilters_KeepsEveryRow()
  {
    // The default applies no requirements at all - a caller that states none gets everything,
    // including the rows whose numbers are unknown.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        Entry("A", prompt: 0.00000004m),
        Entry("B", prompt: 0.0000003m, cacheRead: null),
    ];

    Assert.Equal(2, ProviderRanking.Rank(entries, By(ProviderDimension.Reliability)).Count);
  }

  [Fact]
  public void Rank_ObservedLatency_BeatsTheCatalogFigure()
  {
    // The catalog's latency fields are structurally dead (issue #293: null on all 52
    // endpoints measured live), so our own measurement is the honest source and wins when
    // both exist.
    RankableProvider measured = new(WithLatency("Measured", 5_000), ObservedLatencyMs: 300);
    RankableProvider catalogOnly = RankableProvider.Of(WithLatency("CatalogOnly", 400));

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank([measured, catalogOnly],
        By(ProviderDimension.Latency));

    Assert.Equal(["Measured", "CatalogOnly"],
        ranked.Select(row => row.Entry.ProviderName));
    Assert.Equal(300, ranked[0].LatencyMs);
  }

  [Fact]
  public void Rank_NoObservations_FallsBackToTheCatalogFigures()
  {
    // A provider we never called is not unrankable: the catalog's figure is a weaker source,
    // not an absent one.
    IReadOnlyList<RankableProvider> providers =
    [
        RankableProvider.Of(WithLatency("Slow", 900)),
        RankableProvider.Of(WithLatency("Fast", 300)),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(providers,
        By(ProviderDimension.Latency));

    Assert.Equal(["Fast", "Slow"], ranked.Select(row => row.Entry.ProviderName));
    Assert.Equal(300, ranked[0].LatencyMs);
  }

  [Fact]
  public void Rank_EqualRows_FallToModelThenProviderSoTheOrderIsStable()
  {
    // A comparison that reshuffled equal rows between renders would look like it was
    // reacting to something.
    IReadOnlyList<ModelProviderEntry> entries = [Entry("Zeta"), Entry("Alpha"), Entry("Mu")];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice));

    Assert.Equal(["Alpha", "Mu", "Zeta"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_NoPriorities_StillYieldsADeterministicOrder()
  {
    // 'No stated preference' is a legitimate input; it must not mean 'arbitrary order'.
    IReadOnlyList<ModelProviderEntry> entries = [Entry("Zeta"), Entry("Alpha")];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        new RankingCriteria([], HitRate));

    Assert.Equal(["Alpha", "Zeta"], ranked.Select(row => row.Entry.ProviderName));
  }

  [Fact]
  public void Rank_EveryRowFailingAFilter_ReturnsEmptyRatherThanRelaxingIt()
  {
    // An empty result is an ANSWER: nothing met the requirements the caller set, and the
    // caller says so. Relaxing a filter the user chose would be the silent substitution this
    // harness refuses everywhere else.
    IReadOnlyList<ModelProviderEntry> entries = [WithContext("Narrow", 128_000)];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice,
            filters: new ProviderFilters(MinContextLength: 1_000_000)));

    Assert.Empty(ranked);
  }

  [Fact]
  public void Rank_CarriesTheEffectivePriceItRankedOn()
  {
    // The price rides the result so no caller recomputes it - a second copy of the formula is
    // what UsageCost's precedent exists to prevent, and a caller recomputing at a different
    // hit rate would display a price the order was not based on.
    IReadOnlyList<ModelProviderEntry> entries = [Entry("Only")];

    RankedProvider row = ProviderRanking.Rank(entries, By(ProviderDimension.EffectiveInputPrice))[0];

    // (1 - 0.9) * 0.0000001 + 0.9 * 0.00000001 = 0.00000001 + 0.000000009.
    Assert.Equal(0.000000019m, row.EffectivePrice.ReadPerInputToken);
    Assert.Equal(HitRate, row.EffectivePrice.HitRate);
  }

  [Fact]
  public void Rank_ImplicitCachingUnsupported_DoesNotWinOnAFakeDiscount()
  {
    // D3 reaching the ranking: the provider advertising the deepest cache discount is the
    // cheapest on paper and the most expensive in practice, because it never caches. At a 90%
    // hit rate the honest cost is its full prompt price.
    IReadOnlyList<ModelProviderEntry> entries =
    [
        Entry("NoCacheCheapOnPaper", prompt: 0.00000005m, cacheRead: 0.0000000001m)
            with { SupportsImplicitCaching = false },
        Entry("RealCache"),
    ];

    IReadOnlyList<RankedProvider> ranked = ProviderRanking.Rank(entries,
        By(ProviderDimension.EffectiveInputPrice));

    Assert.Equal("RealCache", ranked[0].Entry.ProviderName);
  }

  [Fact]
  public void Rank_PromptTokens_SelectsTheLadderRungBeforeComparing()
  {
    // Issue #293's ladder reaching the ranking: the laddered provider is cheaper for a short
    // call and dearer for a long one, and the comparison must reflect which call the session
    // is actually making.
    ModelProviderEntry laddered = Entry("Laddered", prompt: 0.00000005m) with
    {
      PriceOverrides = [new PriceOverride(200_000, 0.0000009m, 0.00000009m, null, null)],
    };
    IReadOnlyList<ModelProviderEntry> entries =
    [
        laddered,
        Entry("Flat", prompt: 0.0000002m),
    ];

    IReadOnlyList<RankedProvider> shortCall = ProviderRanking.Rank(entries,
        new RankingCriteria([ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice)],
            HitRate, PromptTokens: 1_000));
    IReadOnlyList<RankedProvider> longCall = ProviderRanking.Rank(entries,
        new RankingCriteria([ProviderPriority.Ascending(ProviderDimension.EffectiveInputPrice)],
            HitRate, PromptTokens: 300_000));

    Assert.Equal("Laddered", shortCall[0].Entry.ProviderName);
    Assert.Equal("Flat", longCall[0].Entry.ProviderName);
  }

  [Fact]
  public void Rank_NullArguments_Throw()
  {
    _ = Assert.Throws<ArgumentNullException>(()
        => ProviderRanking.Rank((IReadOnlyList<ModelProviderEntry>)null!,
            By(ProviderDimension.Latency)));
    _ = Assert.Throws<ArgumentNullException>(()
        => ProviderRanking.Rank([Entry("A")], null!));
  }
}
