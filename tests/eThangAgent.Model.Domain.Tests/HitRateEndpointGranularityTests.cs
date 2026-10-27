namespace eThangAgent.ModelDomain.Tests;

/// <summary>THE MEASURED RUNG MUST BIND TO THE ENDPOINT THAT SERVED (issue #296 follow-up).
///     <para>THE DEFECT THIS PINS: <see cref="HitRateResolution"/> looked the measured rate up
///     by provider NAME (<c>report.Lookup(entry.ModelId, entry.ProviderName)</c>) while the price
///     is keyed per ENDPOINT. One model's endpoints are priced differently and cache
///     differently, so every variant of a provider received the SAME blended rate and was then
///     priced at its own different price - a number describing no endpoint at all. Measured
///     live 2026-10-27: DeepInfra serves <c>deepinfra/fp8/flex</c>, <c>deepinfra/fp8</c> and
///     <c>deepinfra/fp8/priority</c> at $0.0784, $0.098 and $0.147 per million prompt tokens,
///     and all three resolved the single blended 97.95% the name-keyed lookup produced.</para>
///     <para>WHY THE COMPARISON ITSELF DEPENDS ON THIS: the effective input price is
///     <c>(1-h)*prompt + h*cacheRead</c>, so a blended rate can invert the ranking between two
///     endpoints. Morph at 98.5% beat DeepInfra flex at 97%; at the SAME rate flex wins by 5%.
///     The rate is not a detail of the display, it decides the order.</para></summary>
public class HitRateEndpointGranularityTests
{
  private const string Model = "deepseek/deepseek-v4.1-flash-1017";

  /// <summary>One DeepInfra endpoint as the catalog emits it: the same provider NAME, a
  ///     distinct routing SLUG, and its own prices. The prices differ, which is exactly why
  ///     one blended rate cannot describe all three.</summary>
  private static ModelProviderEntry Endpoint(string slug, decimal promptPrice, decimal cacheReadPrice)
  {
    ModelProviderEntry entry = new(
        Model, "DeepInfra", promptPrice, cacheReadPrice, 0.0000005m, 163_840, 8192,
        SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
        IntelligenceScore: 39.5, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
        UptimeLast30m: 100, SupportsImplicitCaching: true);
    return entry with { Variant = new EndpointVariant(slug) };
  }

  /// <summary>The ledger's measurements: one row per SERVED ENDPOINT, each with its own
  ///     token counts. This is the shape the report produces once usage rows carry an endpoint
  ///     identity.</summary>
  private static ProviderPerformanceReport Report(params ProviderPerformance[] rows) =>
      new(UsageScope.Workspace, null, rows.Sum(r => r.InputTokens), rows.Sum(r => r.CachedInputTokens),
          rows.Sum(r => r.Calls), 0, rows);

  private static ProviderPerformance Measured(string? endpoint, long input, long cached) =>
      new(Model, "DeepInfra", 4000d, 9000d, 800d, 60d, 10, 10, 10, 0, 10, input, cached,
          ServedEndpoint: endpoint);

  // THE PREDICTION: three endpoints of ONE provider, three DIFFERENT measured rates, so each
  // row must project at its own. Before the fix every row resolved the blended figure.
  [Fact]
  public void Resolve_KeysTheMeasuredRungByEndpoint_NotByProviderName()
  {
    ModelProviderEntry flex = Endpoint("deepinfra/fp8/flex", 0.0000000784m, 0.000000002352m);
    ModelProviderEntry plain = Endpoint("deepinfra/fp8", 0.000000098m, 0.00000000294m);
    ModelProviderEntry priority = Endpoint("deepinfra/fp8/priority", 0.000000147m, 0.00000000441m);
    ModelProviderEntry[] entries = [flex, plain, priority];

    // flex was hit heavily, priority barely: the difference is what a blended rate erases.
    ProviderPerformanceReport report = Report(
        Measured("deepinfra/fp8/flex", 1_000_000, 990_000),      // 99.0%
        Measured("deepinfra/fp8", 1_000_000, 500_000),           // 50.0%
        Measured("deepinfra/fp8/priority", 1_000_000, 100_000)); // 10.0%

    HitRateLookup lookup = HitRateResolution.Resolve(entries, report, null,
        HitRateAssumption.Fallback);

    Assert.Equal(0.99m, lookup.For(flex).Rate);
    Assert.Equal(0.50m, lookup.For(plain).Rate);
    Assert.Equal(0.10m, lookup.For(priority).Rate);
  }

  // AN ENDPOINT WE NEVER CALLED MUST NOT INHERIT ANOTHER ENDPOINT'S RATE. This is the
  // sharpest form of the defect: one provider name, one measured endpoint, one never used -
  // and the unused one was projected at the other's figure as though it had been measured.
  [Fact]
  public void Resolve_AnUnmeasuredEndpoint_DoesNotInheritASiblingEndpointsRate()
  {
    ModelProviderEntry flex = Endpoint("deepinfra/fp8/flex", 0.0000000784m, 0.000000002352m);
    ModelProviderEntry priority = Endpoint("deepinfra/fp8/priority", 0.000000147m, 0.00000000441m);

    // Only flex was ever called.
    ProviderPerformanceReport report = Report(Measured("deepinfra/fp8/flex", 1_000_000, 990_000));

    HitRateLookup lookup = HitRateResolution.Resolve([flex, priority], report, null,
        HitRateAssumption.Fallback);

    Assert.Equal(0.99m, lookup.For(flex).Rate);
    // priority has no evidence of its own, so it falls to the stated default rather than
    // wearing flex's 99% as a measurement.
    Assert.Equal(HitRateAssumption.Fallback.Rate, lookup.For(priority).Rate);
    Assert.Equal(HitRateSource.Default, lookup.For(priority).Source);
  }

  // AN ENDPOINT WE NEVER CALLED IS NOT 'NOT MEASURED BY US': the published figure for that
  // exact endpoint is strictly better evidence than a default, and it must still be reachable
  // once the measured rung stops answering for every name-sharing row.
  [Fact]
  public void Resolve_AnUnmeasuredEndpoint_FallsThroughToItsPublishedRate()
  {
    ModelProviderEntry flex = Endpoint("deepinfra/fp8/flex", 0.0000000784m, 0.000000002352m);
    ModelProviderEntry plain = Endpoint("deepinfra/fp8", 0.000000098m, 0.00000000294m);
    ModelProviderEntry[] entries = [flex, plain];

    ProviderPerformanceReport report = Report(Measured("deepinfra/fp8/flex", 1_000_000, 990_000));
    Dictionary<string, PublishedProviderStats> published = new(StringComparer.Ordinal)
    {
      [plain.Key] = PublishedProviderStats.Create(0.62m, null, null, 9_500_000_000),
    };

    HitRateLookup lookup = HitRateResolution.Resolve(entries, report, published,
        HitRateAssumption.Fallback);

    Assert.Equal(0.99m, lookup.For(flex).Rate);
    Assert.Equal(HitRateSource.Workspace, lookup.For(flex).Source);
    Assert.Equal(0.62m, lookup.For(plain).Rate);
    Assert.Equal(HitRateSource.Published, lookup.For(plain).Source);
  }

  // LEGACY: a usage row recorded before rows carried an endpoint says only the provider name.
  // It must keep resolving for a name-only pin rather than silently becoming 'not measured'.
  [Fact]
  public void Resolve_ANameOnlyMeasurement_StillAnswersForANamePin()
  {
    ModelProviderEntry pinnedByName = new(
        Model, "DeepInfra", 0.0000000784m, 0.000000002352m, 0.0000005m, 163_840, 8192,
        SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
        IntelligenceScore: 39.5, CodingScore: null, AgenticScore: null,
        LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
        UptimeLast30m: 100, SupportsImplicitCaching: true);

    // A legacy row carries no endpoint: ServedEndpoint is null and only the stamped provider
    // name survives, which is precisely the shape this rung exists for.
    ProviderPerformanceReport report = Report(Measured(null, 1_000_000, 975_000));

    HitRateLookup lookup = HitRateResolution.Resolve([pinnedByName], report, null,
        HitRateAssumption.Fallback);

    Assert.Equal(0.975m, lookup.For(pinnedByName).Rate);
    Assert.Equal(HitRateSource.Workspace, lookup.For(pinnedByName).Source);
  }
}
