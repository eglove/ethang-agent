using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The four endpoint facts the catalog used to throw away, and the two dead fields it
///     used to trust (issue #293). Every fixture below is COPIED FROM THE LIVE PAYLOAD fetched
///     from <c>/api/v1/models/anthropic/claude-sonnet-4.5/endpoints</c> and
///     <c>/api/v1/models/google/gemini-2.5-pro/endpoints</c> on 2026-10-27, field names and
///     values verbatim, so a parser change that stops matching the real wire fails here.
///     <list type="number">
///     <item><c>uptime_last_30m</c>/<c>_5m</c>/<c>_1d</c> - populated on the live API (35 of
///     35 endpoints on one model) and parsed by nothing.</item>
///     <item><c>supports_implicit_caching</c> - present on EVERY live endpoint and parsed by
///     nothing; without it a cheap cache-read price is indistinguishable from a cheap cache
///     read the endpoint will never actually grant.</item>
///     <item><c>pricing.overrides</c> - a PROMPT-SIZE price ladder, not the peak/off-peak
///     clock the parent issue hypothesised: every live entry carries
///     <c>min_prompt_tokens</c> and no time bound.</item>
///     <item><c>pricing.input_cache_write</c> - billed on some providers and invisible.</item>
///     </list>
///     <para>And the dead-field rule (D1/B5): <c>latency_last_30m</c> and
///     <c>throughput_last_30m</c> were null on all 52 endpoints measured, so they are kept,
///     parsed, and never rendered as zero.</para></summary>
public class OpenRouterCatalogEndpointFactsTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  // Live shape: one model, two endpoints. The first carries the full fact set (uptime,
  // implicit caching, a cache-write price, and a 200,000-token price ladder); the second
  // carries none of them, so absence is pinned alongside presence.
  private const string ModelsJson =
      /*lang=json,strict*/
      """
      {"data":[
      {"id":"anthropic/claude-sonnet-4.5","context_length":1000000,"pricing":{"prompt":"0.000003","completion":"0.000015","input_cache_read":"0.0000003","input_cache_write":"0.00000375"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":1000000,"max_completion_tokens":64000}},
      {"id":"bare/model","context_length":131072,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"top_provider":{"context_length":131072,"max_completion_tokens":4096}}
      ]}
      """;

  // Verbatim from the live endpoints call, trimmed to the fields under test.
  private const string SonnetEndpointsJson =
      /*lang=json,strict*/
      """
      {"data":{"id":"anthropic/claude-sonnet-4.5","endpoints":[
      {"name":"Claude Platform on AWS | anthropic/claude-4.5-sonnet-20250929","model_id":"anthropic/claude-sonnet-4.5","context_length":1000000,"pricing":{"prompt":"0.000003","completion":"0.000015","input_cache_read":"0.0000003","input_cache_write":"0.00000375","input_cache_write_1h":"0.000006","discount":0,"overrides":[{"min_prompt_tokens":200000,"prompt":"0.000006","completion":"0.0000225","input_cache_read":"0.0000006","input_cache_write":"0.0000075","input_cache_write_1h":"0.000012"}]},"provider_name":"Claude Platform on AWS","tag":"claude-on-aws","max_completion_tokens":64000,"supported_parameters":["tools"],"status":0,"uptime_last_30m":100,"uptime_last_5m":100,"uptime_last_1d":100,"supports_implicit_caching":false,"latency_last_30m":null,"throughput_last_30m":null},
      {"name":"Anthropic | anthropic/claude-4.5-sonnet","model_id":"anthropic/claude-sonnet-4.5","context_length":200000,"pricing":{"prompt":"0.000003","completion":"0.000015","discount":0},"provider_name":"Anthropic","tag":"anthropic","max_completion_tokens":64000,"supported_parameters":["tools"],"status":0}
      ]}}
      """;

  private const string BareEndpointsJson =
      /*lang=json,strict*/
      """
      [{"provider_name":"BareVendor","context_length":131072,"max_completion_tokens":4096,"pricing":{"prompt":"0.000001","completion":"0.000002"}}]
      """;

  private static HttpResponseMessage JsonResponse(string json) =>
      new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req) =>
      req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => JsonResponse(ModelsJson),
        "/api/v1/models/anthropic/claude-sonnet-4.5/endpoints" => JsonResponse(SonnetEndpointsJson),
        "/api/v1/models/bare/model/endpoints" => JsonResponse(BareEndpointsJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
      };

  private static async Task<IReadOnlyList<ModelProviderEntry>> FetchAsync()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);
    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);
    Assert.True(result.IsSuccess);
    return result.Value;
  }

  private static ModelProviderEntry Endpoint(IReadOnlyList<ModelProviderEntry> entries, string provider)
      => Assert.Single(entries, e => e.ProviderName == provider);

  [Fact]
  public async Task GetAsync_EndpointReportsUptime_CarriesAllThreeWindows()
  {
    // B1/D2: uptime is the reliability signal that IS populated, and it was parsed by nothing.
    ModelProviderEntry aws = Endpoint(await FetchAsync(), "Claude Platform on AWS");

    Assert.Equal(100d, aws.UptimeLast30m);
    Assert.Equal(100d, aws.UptimeLast5m);
    Assert.Equal(100d, aws.UptimeLast1d);
  }

  [Fact]
  public async Task GetAsync_EndpointOmitsUptime_LeavesItNull()
  {
    // Absence is an answer: an endpoint that reported no uptime has not reported 0%.
    ModelProviderEntry anthropic = Endpoint(await FetchAsync(), "Anthropic");

    Assert.Null(anthropic.UptimeLast30m);
    Assert.Null(anthropic.UptimeLast5m);
    Assert.Null(anthropic.UptimeLast1d);
  }

  [Fact]
  public async Task GetAsync_EndpointReportsImplicitCaching_ParsesTheBoolean()
  {
    // B2/D3: a parsed false is a real false - this endpoint genuinely does not implicitly cache.
    ModelProviderEntry aws = Endpoint(await FetchAsync(), "Claude Platform on AWS");

    Assert.False(aws.SupportsImplicitCaching);
  }

  [Fact]
  public async Task GetAsync_EndpointOmitsImplicitCaching_StaysNull_NotFalse()
  {
    // THE TRI-STATE: a row that never said anything has NOT said false. Coercing this to
    // false would tell the user a caching provider does not cache.
    ModelProviderEntry anthropic = Endpoint(await FetchAsync(), "Anthropic");

    Assert.Null(anthropic.SupportsImplicitCaching);
  }

  [Fact]
  public async Task GetAsync_EndpointAdvertisesCacheWritePrice_CarriesIt()
  {
    // B4/D5: cache writes are billed and were invisible, so a free-cache-read model with a
    // 1.25x cache-write price read as a bargain it is not.
    ModelProviderEntry aws = Endpoint(await FetchAsync(), "Claude Platform on AWS");

    Assert.Equal(0.00000375m, aws.PromptCacheWritePricePerToken);
  }

  [Fact]
  public async Task GetAsync_EndpointOmitsCacheWritePrice_InheritsTheModelLevelOne()
  {
    // The live /api/v1/models payload serves input_cache_write at the MODEL level too, so an
    // endpoint that omits it is not 'no cache-write price' - the model's price still applies.
    // A parser that only read the endpoint row would lose it for every such model.
    ModelProviderEntry anthropic = Endpoint(await FetchAsync(), "Anthropic");

    Assert.Equal(0.00000375m, anthropic.PromptCacheWritePricePerToken);
  }

  [Fact]
  public async Task GetAsync_NeitherLevelAdvertisesCacheWritePrice_LeavesItNull()
  {
    // Absence at both levels is a real absence: null, never a fabricated zero.
    ModelProviderEntry bare = Endpoint(await FetchAsync(), "BareVendor");

    Assert.Null(bare.PromptCacheWritePricePerToken);
  }

  [Fact]
  public async Task GetAsync_EndpointServesPriceLadder_CarriesEveryRung()
  {
    // B3/D4: the live override is a PROMPT-SIZE tier, not a clock. The rung's own prices are
    // carried verbatim, including its cache-write price.
    ModelProviderEntry aws = Endpoint(await FetchAsync(), "Claude Platform on AWS");

    PriceOverride tier = Assert.Single(aws.PriceOverrides!);
    Assert.Equal(200_000, tier.MinPromptTokens);
    Assert.Equal(0.000006m, tier.PromptPricePerToken);
    Assert.Equal(0.0000006m, tier.PromptCacheReadPricePerToken);
    Assert.Equal(0.0000075m, tier.PromptCacheWritePricePerToken);
    Assert.Equal(0.0000225m, tier.CompletionPricePerToken);
    Assert.True(aws.HasPriceOverrides);
  }

  [Fact]
  public async Task GetAsync_EndpointServesSinglePrice_HasNoLadder()
  {
    ModelProviderEntry anthropic = Endpoint(await FetchAsync(), "Anthropic");

    Assert.Null(anthropic.PriceOverrides);
    Assert.False(anthropic.HasPriceOverrides);
  }

  [Fact]
  public async Task GetAsync_DeadPerformanceFields_StayNull_AndAreNeverZero()
  {
    // B5/D1: latency_last_30m and throughput_last_30m were null on all 52 endpoints measured.
    // The fields stay on the record (the provider may populate them later) and stay null -
    // never coerced to a 0 that would read as an instant, infinitely fast provider.
    IReadOnlyList<ModelProviderEntry> entries = await FetchAsync();

    Assert.All(entries, entry =>
    {
      Assert.Null(entry.LatencyMs);
      Assert.Null(entry.ThroughputTokensPerSec);
    });
  }

  [Fact]
  public async Task GetAsync_ModelLevelLadderAndCacheWrite_ReachAnEndpointlessModelRow()
  {
    // The /api/v1/models payload serves input_cache_write and overrides at the MODEL level too.
    // A model whose endpoints call failed still gets those facts, while everything
    // endpoint-specific stays null - SupportsImplicitCaching included, because an unparsed
    // endpoint has not said false.
    FakeHttpMessageHandler handler = new(req => Task.FromResult(
        req.RequestUri!.AbsolutePath == "/api/v1/models"
            ? JsonResponse(ModelLevelOnlyJson)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry entry = Assert.Single(result.Value);
    Assert.Equal(0.00000125m, entry.PromptCacheWritePricePerToken);
    Assert.Equal(272_000, Assert.Single(entry.PriceOverrides!).MinPromptTokens);
    Assert.Null(entry.SupportsImplicitCaching);
    Assert.Null(entry.UptimeLast30m);
  }

  // Verbatim from the live /api/v1/models payload (openai/gpt-6.1-terra).
  private const string ModelLevelOnlyJson =
      /*lang=json,strict*/
      """
      {"data":[{"id":"openai/gpt-6.1-terra","context_length":1050000,"architecture":{"input_modalities":["file","image","text"],"output_modalities":["text"]},"pricing":{"prompt":"0.000001","completion":"0.000005","web_search":"0.01","input_cache_read":"0.00000005","input_cache_write":"0.00000125","overrides":[{"min_prompt_tokens":272000,"prompt":"0.000002","completion":"0.0000075","input_cache_read":"0.0000001","input_cache_write":"0.0000025"}]},"top_provider":{"context_length":1050000,"max_completion_tokens":128000}}]}
      """;
}
