namespace eThangAgent.ModelDomain.Tests;

/// <summary>The provider comparison (issue #296, D1-D5): two levels of choice, the effective
///     price as the primary figure, every unavailable value rendered as unavailable, presets
///     that reorder without hiding, and a hit-rate assumption that changes the answer and is
///     always named.
///     <para>THE FIXTURE IS THE PARENT ISSUE'S OWN CASE: for
///     <c>deepseek/deepseek-v4.1-flash-1017</c> the cheapest PROMPT price (Reka, $0.04/M)
///     is the most expensive choice at a 97% hit rate, while DeepInfra at double the prompt
///     price costs a quarter as much. A picker that ranked on the prompt price would
///     recommend the wrong provider, which is exactly the defect this surface replaces.</para>
///     </summary>
public class ProviderComparisonTests
{
  private const string Model = "deepseek/deepseek-v4.1-flash-1017";

  /// <summary>Reka: the cheapest prompt price and the worst cache ratio on the model.
  ///     $0.04/M prompt, $0.03/M cache read (25% off), $0.50/M completion.</summary>
  private static ModelProviderEntry Reka(double? uptime = 99.9, bool? caches = true) => new(
      Model, "Reka", 0.00000004m, 0.00000003m, 0.0000005m, 163_840, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: 40, CodingScore: 41, AgenticScore: 39,
      LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
      UptimeLast30m: uptime, SupportsImplicitCaching: caches);

  /// <summary>DeepInfra: double the prompt price, a 90% cache discount - the cheap choice
  ///     once the hit rate is real.</summary>
  private static ModelProviderEntry DeepInfra(double? uptime = 99.5, bool? caches = true) => new(
      Model, "DeepInfra", 0.00000008m, 0.000000008m, 0.0000005m, 163_840, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: 40, CodingScore: 41, AgenticScore: 39,
      LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
      UptimeLast30m: uptime, SupportsImplicitCaching: caches);

  private static IReadOnlyList<ProviderEvidenceRow> Rows(
      IReadOnlyList<ModelProviderEntry> entries,
      ProviderPerformanceReport? report = null,
      decimal hitRate = 0.97m,
      ProviderPreset preset = ProviderPreset.Cheapest)
      => ProviderComparison.Rows(entries, report,
          HitRateAssumption.Manual(hitRate), preset);

  // ---- B1/D1: the provider level exists and carries its own evidence ----

  [Fact]
  public void Rows_ReturnsOneRowPerProviderEndpoint()
  {
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()]);

    Assert.Equal(2, rows.Count);
    Assert.Contains(rows, row => row.ProviderName == "Reka");
    Assert.Contains(rows, row => row.ProviderName == "DeepInfra");
    Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Evidence)));
  }

  // ---- B2/D2: the effective price is primary, the prompt price secondary ----

  [Fact]
  public void EffectivePrice_IsThePrimaryFigure_AndThePromptPriceIsSecondary()
  {
    ProviderEvidenceRow deepInfra = Rows([Reka(), DeepInfra()])
        .First(row => row.ProviderName == "DeepInfra");

    // (1-0.97)*$0.08 + 0.97*$0.008 = $0.01016/M, versus a $0.08/M prompt price.
    Assert.Equal("$0.0102/M effective", deepInfra.EffectivePriceText);
    Assert.Equal("$0.08/M prompt", deepInfra.PromptPriceText);
    Assert.StartsWith(deepInfra.EffectivePriceText, deepInfra.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void AtAHighHitRate_TheCheapPromptPriceLoses()
  {
    // The parent issue's headline: Reka is cheapest on the prompt price and most expensive
    // in practice. Ranking on the effective price is what makes the list honest.
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()]);

    Assert.Equal("DeepInfra", rows[0].ProviderName);
    Assert.Equal("Reka", rows[1].ProviderName);
  }

  // ---- B4/D6: the hit rate is an assumption that changes the answer ----

  [Fact]
  public void AtAZeroHitRate_TheCheapPromptPriceWins()
  {
    // Same catalog, same preset, different assumption: $0.04/M beats $0.08/M when nothing
    // is cached. The ranking is a function of the stated assumption, never of the catalog
    // alone.
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()], hitRate: 0m);

    Assert.Equal("Reka", rows[0].ProviderName);
  }

  [Fact]
  public void EveryRow_CarriesTheAssumptionItWasPricedAt()
  {
    HitRateAssumption assumption = HitRateAssumption.Manual(0.42m);
    IReadOnlyList<ProviderEvidenceRow> rows = ProviderComparison.Rows(
        [Reka(), DeepInfra()], null, assumption, ProviderPreset.Cheapest);

    Assert.All(rows, row => Assert.Same(assumption, row.Assumption));
  }

  // ---- B3/D5: unmeasured renders as unmeasured ----

  [Fact]
  public void AnUnmeasuredLatency_RendersNotMeasured_NeverZero()
  {
    // The catalog's latency fields are structurally dead (issue #293) and this workspace's
    // ledger has no timed rows yet, so this is the CURRENT production state - not an edge
    // case. A picker that printed 0 ms would tell the user every provider responds instantly.
    ProviderEvidenceRow row = Assert.Single(Rows([Reka()]));

    Assert.Equal(ProviderPerformanceText.NotMeasured, row.LatencyText);
    Assert.Equal(ProviderPerformanceText.NotMeasured, row.ThroughputText);
    Assert.DoesNotContain("0 ms", row.Evidence, StringComparison.Ordinal);
  }

  [Fact]
  public void AnAllUnmeasuredReport_StillRendersEveryRow()
  {
    // The measured state of this workspace's ledger (11,435 usage rows, zero with a
    // DurationMs). The picker must render it as an ordinary picker, not as an empty or
    // failed surface.
    ProviderPerformanceReport report = new(UsageScope.Session, null, 0, 0, 0, 0, []);
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()], report);

    Assert.Equal(2, rows.Count);
    Assert.All(rows, row =>
    {
      Assert.Equal(ProviderPerformanceText.NotMeasured, row.LatencyText);
      Assert.Equal(string.Empty, row.SampleText);
      Assert.False(string.IsNullOrWhiteSpace(row.EffectivePriceText));
    });
  }

  [Fact]
  public void AMeasuredLatency_RendersTheMedianAndItsSampleCount()
  {
    ProviderPerformanceReport report = new(UsageScope.Session, 0.97m, 1000, 970, 3, 0,
    [
      new ProviderPerformance(Model, "Reka", 412d, 900d, 120d, 88d, 3, 3, 3, 0, 3, 1000, 970),
    ]);
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()], report);

    ProviderEvidenceRow reka = rows.First(row => row.ProviderName == "Reka");
    Assert.Equal("412 ms", reka.LatencyText);
    Assert.Equal("88 tok/s", reka.ThroughputText);
    Assert.Contains("3 timed call(s)", reka.SampleText, StringComparison.Ordinal);

    // The pair with no rows keeps the honest absence: matching is by the PAIR, so one
    // provider's measurements never bind to another's row.
    ProviderEvidenceRow deepInfra = rows.First(row => row.ProviderName == "DeepInfra");
    Assert.Equal(ProviderPerformanceText.NotMeasured, deepInfra.LatencyText);
  }

  [Fact]
  public void AnUnknownPrice_RendersPricingUnknown_NeverZero()
  {
    ModelProviderEntry unpriced = Reka() with { PromptPricePerToken = null };
    ProviderEvidenceRow row = Assert.Single(Rows([unpriced]));

    Assert.Equal(ProviderEvidenceRow.PricingUnknown, row.EffectivePriceText);
    Assert.Equal(ProviderEvidenceRow.PricingUnknown, row.PromptPriceText);
    Assert.DoesNotContain("$0", row.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public void AProviderWithoutImplicitCaching_RendersNoCacheSupport()
  {
    ProviderEvidenceRow row = Assert.Single(Rows([Reka(caches: false)]));

    Assert.Equal(ProviderEvidenceRow.NoCacheSupport, row.CacheDiscountText);
    // Without a verified cache the input price IS the prompt price (D3 of #294).
    Assert.Equal("$0.04/M effective", row.EffectivePriceText);
  }

  [Fact]
  public void AProviderWhoseCachingIsUnverified_IsNotCalledUnsupported()
  {
    // A row persisted before the field existed has said NOTHING. Telling the user a
    // caching provider does not cache is the misstatement the tri-state exists to prevent.
    ProviderEvidenceRow row = Assert.Single(Rows([Reka(caches: null)]));

    Assert.Equal(ProviderEvidenceRow.CacheSupportUnverified, row.CacheDiscountText);
    Assert.NotEqual(ProviderEvidenceRow.NoCacheSupport, row.CacheDiscountText);
  }

  [Fact]
  public void AnUnmeasuredUptime_RendersNotMeasured()
  {
    ProviderEvidenceRow row = Assert.Single(Rows([Reka(uptime: null)]));

    Assert.Equal(ProviderPerformanceText.NotMeasured, row.UptimeText);
  }

  [Fact]
  public void CapabilityScores_StayThreeSeparateFigures_WithUnmeasuredMarked()
  {
    ModelProviderEntry unscored = Reka() with { CodingScore = null };
    ProviderEvidenceRow row = Assert.Single(Rows([unscored]));

    Assert.Contains("coding " + ProviderPerformanceText.NotMeasured, row.CapabilityText,
        StringComparison.Ordinal);
    Assert.Contains("intelligence 40", row.CapabilityText, StringComparison.Ordinal);
  }

  [Fact]
  public void AnUnnamedProvider_RendersAMarker_NotABlank()
  {
    ProviderEvidenceRow row = Assert.Single(Rows([Reka() with { ProviderName = null }]));

    Assert.Equal(ProviderEvidenceRow.UnnamedProvider, row.ProviderName);
  }

  // ---- B5/D4: presets reorder, they never hide ----

  [Fact]
  public void EveryPreset_KeepsEveryRow()
  {
    ModelProviderEntry[] entries = [Reka(), DeepInfra()];

    foreach (ProviderPreset preset in Enum.GetValues<ProviderPreset>())
    {
      IReadOnlyList<ProviderEvidenceRow> rows = Rows(entries, preset: preset);
      Assert.Equal(entries.Length, rows.Count);
    }
  }

  [Fact]
  public void TheReliabilityPreset_OrdersByUptime()
  {
    // Reka is the cheaper provider at a high hit rate, so a price-first preset puts it
    // first; the reliability preset must override that and lead with 99.9%.
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([DeepInfra(), Reka()],
        preset: ProviderPreset.MostReliable);

    Assert.Equal("Reka", rows[0].ProviderName);
    Assert.Equal("99.9%", rows[0].UptimeText);
  }

  [Fact]
  public void TheFastestPreset_SortsAnUnmeasuredLatencyLast()
  {
    // Nulls sort last in both directions (D5 of #294): a provider nobody measured is not
    // the fastest one.
    ProviderPerformanceReport report = new(UsageScope.Session, 0.97m, 1000, 970, 1, 0,
    [
      new ProviderPerformance(Model, "Reka", 412d, 900d, 120d, 88d, 1, 1, 1, 0, 1, 1000, 970),
    ]);
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([DeepInfra(), Reka()], report,
        preset: ProviderPreset.Fastest);

    Assert.Equal("Reka", rows[0].ProviderName);
    Assert.Equal(ProviderPerformanceText.NotMeasured, rows[1].LatencyText);
  }

  [Fact]
  public void Presets_AreDistinctOrderings_WithADeterministicTail()
  {
    foreach (ProviderPreset preset in Enum.GetValues<ProviderPreset>())
    {
      IReadOnlyList<ProviderPriority> priorities = ProviderPresets.Priorities(preset);
      Assert.NotEmpty(priorities);
      Assert.Equal(ProviderPresets.Highlighted(preset), priorities[0].Dimension);
      Assert.False(string.IsNullOrWhiteSpace(ProviderPresets.DisplayName(preset)));
      // Reordering only: no preset carries a hard filter (D4).
      Assert.Null(new RankingCriteria(priorities, 0.5m).Filters);
    }
  }

  [Fact]
  public void TheSameCatalogAndPreset_AlwaysYieldTheSameOrder()
  {
    IReadOnlyList<string> first = [.. Rows([Reka(), DeepInfra()]).Select(row => row.ProviderName)];
    IReadOnlyList<string> second = [.. Rows([DeepInfra(), Reka()]).Select(row => row.ProviderName)];

    Assert.Equal(first, second);
  }

  [Fact]
  public void NoReportAtAll_StillRendersTheComparison()
  {
    // A headless host or a failed read leaves the report null. A picker with no history is
    // still a picker: the prices render and the measurements say so.
    IReadOnlyList<ProviderEvidenceRow> rows = Rows([Reka(), DeepInfra()], report: null);

    Assert.Equal(2, rows.Count);
    Assert.All(rows, row => Assert.Equal(ProviderPerformanceText.NotMeasured, row.LatencyText));
  }
}
