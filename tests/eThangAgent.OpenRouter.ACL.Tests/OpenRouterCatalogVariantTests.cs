using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The catalog must keep a provider's endpoints DISTINCT (issue #296 follow-up).
///     <para>THE FIXTURE IS THE LIVE PAYLOAD, measured 2026-10-27: <c>x-ai/grok-4.7</c> serves
///     five endpoints all named "xAI" - <c>xai</c>, <c>xai/zdr</c>, <c>xai/zdr/us</c>,
///     <c>xai/priority</c>, <c>xai/zdr/priority</c> - priced $2, $2, $2.20, $4 and $4 per
///     million prompt tokens. Every one is a different product a user can choose and pin, so
///     collapsing them by provider name discarded four real choices.</para></summary>
public class OpenRouterCatalogVariantTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private const string ModelsJson =
      /*lang=json,strict*/
      """{"data":[{"id":"x-ai/grok-4.7","canonical_slug":"x-ai/grok-4.7","context_length":500000,"pricing":{"prompt":"0.000002","completion":"0.000006"},"architecture":{"input_modalities":["text"],"output_modalities":["text"],"tokenizer":"Grok"},"supported_parameters":["tools"]}]}""";

  // Five endpoints, ONE provider name. The tag is the only field that distinguishes them.
  private const string EndpointsJson =
      /*lang=json,strict*/
      """{"data":{"id":"x-ai/grok-4.7","endpoints":[{"provider_name":"xAI","tag":"xai","context_length":500000,"pricing":{"prompt":"0.000002","completion":"0.000006","input_cache_read":"0.0000005"},"quantization":"unknown","service_tier":null,"supports_implicit_caching":true,"uptime_last_30m":100},{"provider_name":"xAI","tag":"xai/zdr","context_length":500000,"pricing":{"prompt":"0.000002","completion":"0.000006","input_cache_read":"0.0000005"},"quantization":"unknown","service_tier":null,"supports_implicit_caching":true,"uptime_last_30m":100},{"provider_name":"xAI","tag":"xai/zdr/us","context_length":500000,"pricing":{"prompt":"0.0000022","completion":"0.0000066","input_cache_read":"0.00000055"},"quantization":"unknown","service_tier":null,"supports_implicit_caching":true,"uptime_last_30m":100},{"provider_name":"xAI","tag":"xai/priority","context_length":500000,"pricing":{"prompt":"0.000004","completion":"0.000012","input_cache_read":"0.000001"},"quantization":"unknown","service_tier":"priority","supports_implicit_caching":true},{"provider_name":"xAI","tag":"xai/zdr/priority","context_length":500000,"pricing":{"prompt":"0.000004","completion":"0.000012","input_cache_read":"0.000001"},"quantization":"unknown","service_tier":"priority","supports_implicit_caching":true}]}}""";

  private static HttpResponseMessage JsonResponse(string json) =>
      new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req)
      => req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => JsonResponse(ModelsJson),
        var path when path.EndsWith("/endpoints", StringComparison.Ordinal) => JsonResponse(EndpointsJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
      };

  [Fact]
  public async Task OneRoutableEndpointServedTwice_KeepsTheCheaperPrice()
  {
    // MEASURED LIVE 2026-10-27: qwen/qwen3-235b-a22b-2507 serves google-vertex/us-south1 TWICE,
    // at $0.22/M and $0.25/M. That is the API disagreeing with itself, not two products -
    // provider.only can only name the tag, so the router reaches ONE endpoint and the user
    // cannot choose between the two rows. The catalog therefore keeps one row per routable
    // endpoint, and the cheapest advertised price is the claim that can be acted on.
    // (This is NOT the collapsing variants exist to prevent: that merged DIFFERENT endpoints
    // onto a provider name.)
    const string duplicateModels =
        /*lang=json,strict*/
        """{"data":[{"id":"qwen/qwen3","canonical_slug":"qwen/qwen3","context_length":262144,"pricing":{"prompt":"0.00000022","completion":"0.00000088"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"]}]}""";
    const string duplicateEndpoints =
        /*lang=json,strict*/
        """{"data":{"id":"qwen/qwen3","endpoints":[{"provider_name":"Google","tag":"google-vertex/us-south1","context_length":262144,"pricing":{"prompt":"0.00000025","completion":"0.000001"}},{"provider_name":"Google","tag":"google-vertex/us-south1","context_length":262144,"pricing":{"prompt":"0.00000022","completion":"0.00000088"}}]}}""";
    FakeHttpMessageHandler handler = new(req => Task.FromResult(req.RequestUri!.AbsolutePath switch
    {
      "/api/v1/models" => JsonResponse(duplicateModels),
      var path when path.EndsWith("/endpoints", StringComparison.Ordinal) => JsonResponse(duplicateEndpoints),
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    }));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry entry = Assert.Single(result.Value);
    Assert.Equal(0.00000022m, entry.PromptPricePerToken);
    Assert.Equal("google-vertex/us-south1", entry.RoutingSlug);
  }

  [Fact]
  public async Task TheZeroRetentionList_IsReadAndJoinedToTheCatalogRows()
  {
    // THE PROVIDER'S LIST IS THE AUTHORITY (measured live 2026-11-01): deepseek/deepseek-v4.1-flash-1017
    // has 42 eligible endpoints and not one tag says 'zdr', so eligibility cannot be read from
    // the slug. The list names endpoints by model, provider name, and tag - the same parts the
    // catalog keys on - and a row for a model the catalog does not hold contributes nothing.
    const string zdrJson =
        /*lang=json,strict*/
        """{"data":[{"model_id":"x-ai/grok-4.7","provider_name":"xAI","tag":"xai","safety_retention":false},{"model_id":"x-ai/grok-4.7","provider_name":"xAI","tag":"xai/priority","safety_retention":true},{"model_id":"absent/model","provider_name":"Nobody","tag":"nobody","safety_retention":false}]}""";
    FakeHttpMessageHandler handler = new(req => Task.FromResult(req.RequestUri!.AbsolutePath switch
    {
      "/api/v1/models" => JsonResponse(ModelsJson),
      "/api/v1/endpoints/zdr" => JsonResponse(zdrJson),
      var path when path.EndsWith("/endpoints", StringComparison.Ordinal) => JsonResponse(EndpointsJson),
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    }));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    ZeroDataRetentionList? eligible =
        await client.GetZeroDataRetentionAsync(TestContext.Current.CancellationToken);

    Assert.NotNull(eligible);
    // The plain endpoint is eligible even though its tag says nothing about retention.
    Assert.True(eligible.IsEligible("x-ai/grok-4.7", "xAI", "xai"));
    Assert.True(eligible.ServesModel("x-ai/grok-4.7"));
    // A row for a model the catalog never loaded is not a key anything can route to.
    Assert.False(eligible.IsEligible("absent/model", "Nobody", "nobody"));
    Assert.False(eligible.ServesModel("absent/model"));
  }

  [Fact]
  public async Task AnUnreadableZeroRetentionList_IsNull_NotEmpty()
  {
    // Null is 'not consulted' and empty is 'consulted, nothing eligible'. The restriction is
    // enforced on that difference, so a read fault must never masquerade as proof.
    FakeHttpMessageHandler handler = new(req => Task.FromResult(req.RequestUri!.AbsolutePath switch
    {
      "/api/v1/models" => JsonResponse(ModelsJson),
      "/api/v1/endpoints/zdr" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
      var path when path.EndsWith("/endpoints", StringComparison.Ordinal) => JsonResponse(EndpointsJson),
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    }));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    Assert.Null(await client.GetZeroDataRetentionAsync(TestContext.Current.CancellationToken));
  }

  private static async Task<IReadOnlyList<ModelProviderEntry>> CatalogAsync()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);
    Result<IReadOnlyList<ModelProviderEntry>> result =
        await client.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    return result.Value;
  }

  [Fact]
  public async Task FiveEndpointsSharingOneProviderName_AllSurvive()
  {
    // The defect: 339 of 1509 live rows were discarded because the pair key collapsed them.
    IReadOnlyList<ModelProviderEntry> entries = await CatalogAsync();

    Assert.Equal(5, entries.Count);
    Assert.All(entries, entry => Assert.Equal("xAI", entry.ProviderName));
    Assert.Equal(5, entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
  }

  [Fact]
  public async Task EachEndpointKeepsItsOwnPrice()
  {
    // The surviving row's price used to be whichever endpoint the API listed first, so the
    // $4 priority endpoint and the $2 plain one were indistinguishable.
    IReadOnlyList<ModelProviderEntry> entries = await CatalogAsync();

    ModelProviderEntry plain = entries.Single(e => e.Variant.Slug == "xai");
    ModelProviderEntry priority = entries.Single(e => e.Variant.Slug == "xai/priority");
    Assert.Equal(0.000002m, plain.PromptPricePerToken);
    Assert.Equal(0.000004m, priority.PromptPricePerToken);
  }

  [Fact]
  public async Task EachEndpointCarriesItsRoutableSlugAndAnnotation()
  {
    IReadOnlyList<ModelProviderEntry> entries = await CatalogAsync();

    ModelProviderEntry zdrUs = entries.Single(e => e.Variant.Slug == "xai/zdr/us");
    Assert.Equal("xai/zdr/us", zdrUs.RoutingSlug);
    Assert.True(zdrUs.Variant.IsZeroDataRetention);
    Assert.Equal("us", zdrUs.Variant.Region);
    Assert.Contains("zero data retention", zdrUs.Variant.Describe(), StringComparison.Ordinal);

    ModelProviderEntry priority = entries.Single(e => e.Variant.Slug == "xai/priority");
    Assert.Equal("priority", priority.Variant.ServiceTier);

    // The plain endpoint has nothing to annotate, and says so by rendering nothing.
    ModelProviderEntry plain = entries.Single(e => e.Variant.Slug == "xai");
    Assert.Equal(string.Empty, plain.Variant.Describe());
  }
}
