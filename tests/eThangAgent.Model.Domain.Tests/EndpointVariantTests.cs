namespace eThangAgent.ModelDomain.Tests;

/// <summary>The endpoint variant (issue #296 follow-up): a provider does not serve one
///     endpoint, and the difference between its endpoints is a product decision the user makes.
///     <para>THE FIXTURES ARE THE LIVE PAYLOAD, measured 2026-10-27: <c>x-ai/grok-4.7</c>
///     serves FIVE endpoints all named "xAI" - <c>xai</c>, <c>xai/zdr</c>, <c>xai/zdr/us</c>,
///     <c>xai/priority</c>, <c>xai/zdr/priority</c> - priced $2, $2, $2.20, $4 and $4 per
///     million prompt tokens. Keying on the provider name collapsed all five into one row and
///     discarded four.</para></summary>
public class EndpointVariantTests
{
  [Theory]
  [InlineData("xai", null, null)]
  [InlineData("xai/zdr", null, null)]
  [InlineData("xai/zdr/us", null, null)]
  [InlineData("xai/priority", "priority", null)]
  [InlineData("xai/zdr/priority", "priority", null)]
  [InlineData("deepinfra/fp8", null, "fp8")]
  [InlineData("deepinfra/fp8/priority", "priority", "fp8")]
  [InlineData("mistral/nvfp4/eu", null, "nvfp4")]
  [InlineData("google-vertex/global/flex", "flex", null)]
  [InlineData("amazon-bedrock/us-east-1", null, null)]
  public void Parse_ReadsTheSlugIntoItsParts(string tag, string? tier, string? quant)
  {
    EndpointVariant variant = EndpointVariant.Parse(tag, null, null);

    Assert.Equal(tag, variant.Slug);
    Assert.Equal(tier, variant.ServiceTier);
    Assert.Equal(quant, variant.Quantization);
  }

  [Fact]
  public void Parse_RecognizesZeroDataRetentionAndRegion()
  {
    EndpointVariant variant = EndpointVariant.Parse("xai/zdr/us", null, null);

    Assert.True(variant.IsZeroDataRetention);
    Assert.Equal("us", variant.Region);
  }

  [Fact]
  public void Parse_APlainProviderSlug_ClaimsNoRegion()
  {
    // The provider segment must never be read as a place: 'xai' is two letters, which is
    // exactly the shape the region test accepts, and position is what excludes it.
    EndpointVariant variant = EndpointVariant.Parse("xai", null, null);

    Assert.Null(variant.Region);
    Assert.False(variant.IsZeroDataRetention);
    Assert.False(variant.IsUnspecified);
  }

  [Fact]
  public void Parse_ThePayloadFieldsWinOverTheSlugTokens()
  {
    // service_tier and quantization are served as their own fields; when present they are
    // authoritative and the slug is only a fallback.
    EndpointVariant variant = EndpointVariant.Parse("deepinfra/fp8", "flex", "fp4");

    Assert.Equal("flex", variant.ServiceTier);
    Assert.Equal("fp4", variant.Quantization);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void Parse_NoTag_YieldsAnUnspecifiedVariant(string? tag)
  {
    // A payload with no tags degrades to the previous behaviour rather than inventing one.
    EndpointVariant variant = EndpointVariant.Parse(tag, null, null);

    Assert.Null(variant.Slug);
    Assert.True(variant.IsUnspecified);
  }

  [Fact]
  public void Parse_UnknownQuantization_IsAbsence_NotAValue()
  {
    // 'unknown' is the API's word for 'not stated'. Rendering it would present an absence
    // as a fact - the same rule the nullable prices follow.
    EndpointVariant variant = EndpointVariant.Parse("xai", null, "unknown");

    Assert.Null(variant.Quantization);
  }

  [Fact]
  public void Describe_NamesEveryDistinguishingFact()
  {
    EndpointVariant variant = EndpointVariant.Parse("xai/zdr/us/priority", "priority", "fp8");

    string described = variant.Describe();
    Assert.Contains("zero data retention", described, StringComparison.Ordinal);
    Assert.Contains("region us", described, StringComparison.Ordinal);
    Assert.Contains("priority tier", described, StringComparison.Ordinal);
    Assert.Contains("fp8", described, StringComparison.Ordinal);
  }

  [Fact]
  public void Describe_IsEmptyWhenNothingDistinguishesTheEndpoint()
  {
    // A plain endpoint gets no annotation at all rather than an empty one.
    Assert.Equal(string.Empty, EndpointVariant.Parse("xai", null, null).Describe());
    Assert.Equal(string.Empty, EndpointVariant.Unspecified.Describe());
  }

  [Fact]
  public void Key_DistinguishesEndpointsSharingOneProviderName()
  {
    // THE DEFECT THIS REPLACES: five grok-4.7 endpoints are all named "xAI", so a
    // name-keyed pair collapsed them into one row and 339 of 1509 live catalog rows were
    // silently discarded. The slug is what keeps them apart.
    ModelProviderEntry plain = Entry("xai", 0.000002m);
    ModelProviderEntry zdr = Entry("xai/zdr", 0.000002m);
    ModelProviderEntry priority = Entry("xai/priority", 0.000004m);

    Assert.Equal("xAI", plain.ProviderName);
    Assert.Equal("xAI", zdr.ProviderName);
    Assert.NotEqual(plain.Key, zdr.Key);
    Assert.NotEqual(plain.Key, priority.Key);
    Assert.NotEqual(zdr.Key, priority.Key);
  }

  [Fact]
  public void Key_WithNoTag_FallsBackToThePreviousPairKey()
  {
    // A payload that carries no tags must degrade to exactly what this harness did before,
    // not collapse every endpoint of a provider into one row.
    ModelProviderEntry entry = Entry(null, 0.000002m);

    Assert.Equal("deepseek/deepseek-v4.1-flash-1017:xAI", entry.Key);
  }

  [Fact]
  public void RoutingSlug_IsWhatThePinMustSend()
  {
    // OpenRouter's routing docs: an endpoint slug is provider[/variant][/region][/tier] and
    // provider.only accepts the full slug, so this is the value that reaches ONE endpoint.
    Assert.Equal("xai/zdr/priority", Entry("xai/zdr/priority", 0.000004m).RoutingSlug);
    // With no tag the provider name is the only address available - the previous pin.
    Assert.Equal("xAI", Entry(null, 0.000002m).RoutingSlug);
  }

  private static ModelProviderEntry Entry(string? tag, decimal promptPrice) => new(
      "deepseek/deepseek-v4.1-flash-1017", "xAI", promptPrice, 0.0000005m, 0.000006m, 500_000, 450_000,
      SupportsToolUse: true, SupportsVision: true, SupportsTextOutput: true,
      IntelligenceScore: 46.4, CodingScore: null, AgenticScore: null,
      LatencyMs: null, ThroughputTokensPerSec: null, Description: null,
      UptimeLast30m: 100, SupportsImplicitCaching: true)
  {
    Variant = EndpointVariant.Parse(tag, null, null),
  };

  [Fact]
  public void Describe_PresentsDifferentEndpointsDifferently()
  {
    // The whole point: two endpoints of one provider must not render identically.
    string plain = EndpointVariant.Parse("xai", null, null).Describe();
    string zdr = EndpointVariant.Parse("xai/zdr", null, null).Describe();
    string tier = EndpointVariant.Parse("xai/priority", "priority", null).Describe();

    Assert.NotEqual(plain, zdr);
    Assert.NotEqual(plain, tier);
    Assert.NotEqual(zdr, tier);
  }
}
