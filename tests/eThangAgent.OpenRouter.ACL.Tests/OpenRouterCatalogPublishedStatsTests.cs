using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The published cache-hit rates the catalog client fetches alongside the catalog
///     (issue #296 follow-up). The shapes here are copied from the LIVE payloads measured
///     2026-10-27, including the two facts that make the join delicate: the statistics are
///     keyed by <c>providerSlug</c> (which equals the endpoints API's <c>tag</c>, region
///     included) and the model is addressed by its canonical slug, not its id.</summary>
public class OpenRouterCatalogPublishedStatsTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  // Two endpoints of ONE model whose provider NAMES are identical but whose tags differ by
  // region - the case a name-keyed join would collapse into one row.
  private const string ModelsJson =
      /*lang=json,strict*/
      """{"data":[{"id":"anthropic/claude-sonnet-4.5","canonical_slug":"anthropic/claude-4.5-sonnet-20250929","context_length":1000000,"pricing":{"prompt":"0.000003","completion":"0.000015"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"]}]}""";

  private const string EndpointsJson =
      /*lang=json,strict*/
      """{"data":{"id":"anthropic/claude-sonnet-4.5","endpoints":[{"provider_name":"Amazon Bedrock","tag":"amazon-bedrock","context_length":1000000,"pricing":{"prompt":"0.000003","completion":"0.000015","input_cache_read":"0.0000003"},"supports_implicit_caching":true},{"provider_name":"Amazon Bedrock","tag":"amazon-bedrock/eu-west-1","context_length":1000000,"pricing":{"prompt":"0.0000033","completion":"0.0000165","input_cache_read":"0.00000033"},"supports_implicit_caching":true}]}}""";

  // The published statistics: per-endpoint cache-hit rates, plus a weighted model total that
  // this client deliberately does NOT use (a scope-wide rate is the single-number projection
  // the per-pair resolution exists to replace).
  // The published ENDPOINT payload: it carries the exact provider names the catalog uses
  // (no de-duplication suffixes) but no rates.
  private const string StatsEndpointsJson =
      /*lang=json,strict*/
      """{"data":[{"id":"a","provider_name":"Amazon Bedrock","provider_slug":"amazon-bedrock"},{"id":"b","provider_name":"Amazon Bedrock","provider_slug":"amazon-bedrock/eu-west-1"}]}""";

  private const string StatsJson =
      /*lang=json,strict*/
      """{"data":{"weightedInputPrice":1.51,"weightedOutputPrice":15.0,"weightedCacheHitRate":0.629,"providerSummaries":[{"endpointId":"a","providerName":"Amazon Bedrock","providerSlug":"amazon-bedrock","effectiveInputPrice":1.471,"effectiveOutputPrice":15.0,"cacheHitRate":0.5995,"totalTokens":3784577189},{"endpointId":"b","providerName":"Amazon Bedrock","providerSlug":"amazon-bedrock/eu-west-1","effectiveInputPrice":1.227,"effectiveOutputPrice":16.499,"cacheHitRate":0.7454,"totalTokens":8747997}]}}""";

  private static HttpResponseMessage JsonResponse(string json) =>
      new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req)
  {
    string path = req.RequestUri!.AbsolutePath;
    if (path == "/api/v1/models")
    {
      return JsonResponse(ModelsJson);
    }

    if (path.EndsWith("/endpoints", StringComparison.Ordinal))
    {
      return JsonResponse(EndpointsJson);
    }

    // The permaslug is the CANONICAL slug, not the model id - the live API returns zeros for
    // the id form, which would silently publish a 0% rate for every provider.
    string? permaslug = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["permaslug"];
    return permaslug == "anthropic/claude-4.5-sonnet-20250929"
        ? path switch
        {
          "/api/frontend/v1/stats/effective-pricing" => JsonResponse(StatsJson),
          "/api/frontend/v1/stats/endpoint" => JsonResponse(StatsEndpointsJson),
          _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        }
        : new HttpResponseMessage(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task TwoEndpointsSharingAName_BindToTheOneWithTheMostTraffic()
  {
    // The catalog's pair key is model + provider NAME, so two endpoints of one model that
    // share a name collapse to one key - and the catalog cannot distinguish them either,
    // since it renders two identically-labelled rows. The binding is therefore deterministic
    // by published traffic volume rather than by arrival order: the 9.5B-token endpoint wins
    // over the 34K-token one.
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    _ = await client.GetAsync(TestContext.Current.CancellationToken);
    IReadOnlyDictionary<string, PublishedProviderStats> published =
        await client.GetPublishedStatsAsync(TestContext.Current.CancellationToken);

    PublishedProviderStats bound = Assert.Single(published).Value;
    Assert.Equal(0.5995m, bound.CacheHitRate);
    Assert.Equal(3784577189L, bound.TotalTokens);
  }

  [Fact]
  public async Task PublishedRates_CarryTheRateAndConvertPricesToPerToken()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    _ = await client.GetAsync(TestContext.Current.CancellationToken);
    IReadOnlyDictionary<string, PublishedProviderStats> published =
        await client.GetPublishedStatsAsync(TestContext.Current.CancellationToken);

    PublishedProviderStats bound = Assert.Single(published).Value;
    // The published rate is preserved exactly as served.
    Assert.Equal(0.5995m, bound.CacheHitRate);
    // Prices arrive per-MILLION and are stored per-token, the catalog's own unit.
    Assert.Equal(1.471m / 1_000_000m, bound.EffectiveInputPricePerToken);
    Assert.Equal(15.0m / 1_000_000m, bound.EffectiveOutputPricePerToken);
    Assert.Equal(3784577189L, bound.TotalTokens);
  }

  [Fact]
  public async Task APublishedRate_BecomesAPublishedAssumption_NeverAMeasuredOne()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    _ = await client.GetAsync(TestContext.Current.CancellationToken);
    IReadOnlyDictionary<string, PublishedProviderStats> published =
        await client.GetPublishedStatsAsync(TestContext.Current.CancellationToken);

    HitRateAssumption? assumption = published.Values.First().Assumption;
    Assert.NotNull(assumption);
    Assert.Equal(HitRateSource.Published, assumption.Source);
  }

  [Fact]
  public async Task AStatisticsFault_LeavesTheCatalogUsable_WithNoPublishedRates()
  {
    // These are undocumented frontend endpoints: a shape change must degrade to listed
    // prices plus the documented default, never fail the catalog the picker renders.
    FakeHttpMessageHandler handler = new(req =>
        Task.FromResult(req.RequestUri!.AbsolutePath == "/api/frontend/v1/stats/effective-pricing"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Handler(req)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> catalog =
        await client.GetAsync(TestContext.Current.CancellationToken);
    IReadOnlyDictionary<string, PublishedProviderStats> published =
        await client.GetPublishedStatsAsync(TestContext.Current.CancellationToken);

    Assert.True(catalog.IsSuccess);
    Assert.Equal(2, catalog.Value.Count);
    Assert.Empty(published);
  }

  [Fact]
  public async Task AModelWithNoCanonicalSlug_PublishesNothing_AndStillLoads()
  {
    const string NoSlugModels =
        /*lang=json,strict*/
        """{"data":[{"id":"local/model","context_length":8192,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":[]}]}""";
    FakeHttpMessageHandler handler = new(req =>
        Task.FromResult(req.RequestUri!.AbsolutePath == "/api/v1/models"
            ? JsonResponse(NoSlugModels)
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
    using HttpClient http = new(handler);
    using OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> catalog =
        await client.GetAsync(TestContext.Current.CancellationToken);
    IReadOnlyDictionary<string, PublishedProviderStats> published =
        await client.GetPublishedStatsAsync(TestContext.Current.CancellationToken);

    Assert.True(catalog.IsSuccess);
    Assert.Empty(published);
  }
}
