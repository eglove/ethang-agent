namespace eThangAgent.ModelDomain.Tests;

/// <summary>The per-pair hit-rate lookup (issue #296 follow-up) must be TOTAL over whatever the
///     catalog hands it.
///     <para>THE DEFECT THIS PINS: OpenRouter serves several endpoints under ONE provider name
///     (region and ZDR variants of the same vendor), so the catalog emits several rows whose
///     <see cref="ModelProviderEntry.Key"/> is identical. <c>ToDictionary</c> throws
///     <c>An item with the same key has already been added</c> on the second one, and because
///     the resolution runs while the picker loads, the whole Model Settings dialog died with a
///     raw exception instead of a model list (observed 2026-10-27, key
///     <c>deepseek/deepseek-v4.1-flash-1017:InferenceNet</c>).</para>
///     <para>DUPLICATES LOSE NOTHING: the rate is keyed by the PAIR, so every row sharing a key
///     resolves the same rate. Dropping the duplicate is the whole fix - no information is
///     discarded that the key could have distinguished.</para></summary>
public class HitRateLookupTests
{
  private const string Model = "deepseek/deepseek-v4.1-flash-1017";

  /// <summary>Two catalog rows for one pair: the region/ZDR variants of a single provider.
  ///     The prices differ, which is what makes them distinct rows rather than a duplicate
  ///     parse - and their key is still identical.</summary>
  private static ModelProviderEntry Variant(decimal promptPrice) => new(
      Model, "InferenceNet", promptPrice, 0.0000001m, 0.0000005m, 163_840, 8192,
      SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
      IntelligenceScore: 40, CodingScore: 41, AgenticScore: 39,
      LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
      UptimeLast30m: 99.9, SupportsImplicitCaching: true);

  [Fact]
  public void Create_TwoRatesForOnePair_DoesNotThrow()
  {
    // The exact shape that crashed the picker: one key, two entries.
    HitRateLookup lookup = HitRateLookup.Create(
    [
      new KeyValuePair<string, HitRateAssumption>($"{Model}:InferenceNet",
          HitRateAssumption.FromPublished(0.20m)),
      new KeyValuePair<string, HitRateAssumption>($"{Model}:InferenceNet",
          HitRateAssumption.FromPublished(0.80m)),
    ],
    HitRateAssumption.Fallback);

    // The pair resolves to a real published rate - not the fallback - so the duplicate was
    // absorbed rather than silently dropped from the map.
    HitRateAssumption rate = lookup.For(Variant(0.00000008m));
    Assert.Equal(HitRateSource.Published, rate.Source);
  }

  [Fact]
  public void Resolve_DuplicatePairKeys_ResolvesEveryRow()
  {
    // The production path: the resolution walks the catalog's own rows, which contain the
    // duplicate, so the duplicate reaches Create through Resolve rather than directly.
    ModelProviderEntry[] entries = [Variant(0.00000008m), Variant(0.00000022m)];
    ProviderPerformanceReport report = new(UsageScope.Session, 0.97m, 1000, 970, 2, 0,
    [
      new ProviderPerformance(Model, "InferenceNet", 412d, 900d, 120d, 88d, 2, 2, 2, 0, 2,
          1000, 970),
    ]);

    HitRateLookup lookup = HitRateResolution.Resolve(entries, report, null,
        HitRateAssumption.Fallback);

    // Every row resolves, and to the pair's own MEASURED rate - the strongest rung.
    Assert.All(entries, entry =>
    {
      HitRateAssumption rate = lookup.For(entry);
      Assert.Equal(HitRateSource.Session, rate.Source);
      Assert.Equal(0.97m, rate.Rate);
    });
  }
}
