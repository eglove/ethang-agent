namespace eThangAgent.ModelDomain.Tests;

/// <summary>A usage row names the endpoint it ran on only when the PIN named one (issue #296
///     follow-up).
///     <para>THE DEFECT THIS PINS: the provider stamp on a usage row is the session's pin.
///     When that pin is a bare provider NAME, the stamp is a name - and a name does not
///     identify an endpoint, because one provider serves several at different prices. Those
///     rows must stay on the name rung (honestly ambiguous) rather than be bound to whichever
///     variant the catalog happens to list first, which is what made every DeepInfra row wear
///     the flex price.</para></summary>
public class ProviderPerformanceEndpointBindingTests
{
  private const string Model = "deepseek/deepseek-v4.1-flash-1017";

  private static ModelProviderEntry Endpoint(string provider, string? slug) =>
      new ModelProviderEntry(Model, provider, 0.0000000784m, 0.000000002352m, 0.0000005m,
          163_840, 8192, SupportsToolUse: true, SupportsVision: false, SupportsTextOutput: true,
          IntelligenceScore: 39.5, CodingScore: null, AgenticScore: null,
          LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
          UptimeLast30m: 100, SupportsImplicitCaching: true)
      with
      { Variant = slug is null ? EndpointVariant.Unspecified : new EndpointVariant(slug) };

  private static ProviderPerformance Measured(string? stampedProvider, long input, long cached) =>
      new(Model, stampedProvider, 4000d, 9000d, 800d, 60d, 10, 10, 10, 0, 10, input, cached);

  private static ProviderPerformanceReport Report(params ProviderPerformance[] rows) =>
      new(UsageScope.Workspace, null, rows.Sum(r => r.InputTokens), rows.Sum(r => r.CachedInputTokens),
          rows.Sum(r => r.Calls), 0, rows);

  // A slug stamp names one endpoint, so the measurement binds to exactly that endpoint.
  [Fact]
  public void BindEndpoints_ASlugStamp_BindsToThatEndpoint()
  {
    ProviderPerformanceReport bound = Report(Measured("deepinfra/fp8/flex", 1_000_000, 990_000))
        .BindEndpoints([Endpoint("DeepInfra", "deepinfra/fp8/flex"), Endpoint("DeepInfra", "deepinfra/fp8")]);

    ProviderPerformance row = Assert.Single(bound.Providers);
    Assert.Equal("deepinfra/fp8/flex", row.ServedEndpoint);
    Assert.Equal(0.99m, row.HitRate);
  }

  // THE HEART OF IT: a bare provider name is three endpoints at three prices. Binding it to
  // any one of them is the silent substitution; leaving it unbound is the honest answer.
  [Fact]
  public void BindEndpoints_ABareNameStamp_BindsToNothing()
  {
    ProviderPerformanceReport bound = Report(Measured("DeepInfra", 1_000_000, 990_000))
        .BindEndpoints([
            Endpoint("DeepInfra", "deepinfra/fp8/flex"),
            Endpoint("DeepInfra", "deepinfra/fp8"),
            Endpoint("DeepInfra", "deepinfra/fp8/priority"),
        ]);

    ProviderPerformance row = Assert.Single(bound.Providers);
    Assert.Null(row.ServedEndpoint);
  }

  // A slug naming no catalog row (a stale pin) is no evidence either: inventing a binding
  // would attach a measurement to an endpoint the catalog does not list.
  [Fact]
  public void BindEndpoints_ASlugWithNoCatalogRow_StaysUnbound()
  {
    ProviderPerformanceReport bound = Report(Measured("deepinfra/retired", 1_000_000, 990_000))
        .BindEndpoints([Endpoint("DeepInfra", "deepinfra/fp8/flex")]);

    Assert.Null(Assert.Single(bound.Providers).ServedEndpoint);
  }

  // A CATALOG ROW WITH NO VARIANT TAG HAS NO ENDPOINT IDENTITY. Its RoutingSlug falls back to
  // the provider NAME, and treating that fallback as a slug would bind a name stamp to a name
  // - reintroducing the ambiguity this method exists to remove. Caught by the picker's
  // pre-existing measured-rate tests, pinned here so it cannot come back.
  [Fact]
  public void BindEndpoints_AnUntaggedCatalogRow_IsNotAnEndpointIdentity()
  {
    ProviderPerformanceReport bound = Report(Measured("ProviderA", 1_000_000, 880_000))
        .BindEndpoints([Endpoint("ProviderA", slug: null)]);

    Assert.Null(Assert.Single(bound.Providers).ServedEndpoint);
  }

  // The binding is idempotent: a row that already names its endpoint is left alone, so a
  // second pass cannot re-key it by name and lose the fact.
  [Fact]
  public void BindEndpoints_AlreadyBoundRows_AreUnchanged()
  {
    ProviderPerformanceReport first = Report(Measured("deepinfra/fp8/flex", 1_000_000, 990_000))
        .BindEndpoints([Endpoint("DeepInfra", "deepinfra/fp8/flex")]);

    ProviderPerformanceReport second = first.BindEndpoints([Endpoint("DeepInfra", "deepinfra/fp8")]);

    Assert.Equal("deepinfra/fp8/flex", Assert.Single(second.Providers).ServedEndpoint);
  }
}
