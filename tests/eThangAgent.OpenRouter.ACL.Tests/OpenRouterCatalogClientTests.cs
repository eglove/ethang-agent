using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterCatalogClientTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private const string SampleModelsJson =
      /*lang=json,strict*/
      """{"data":[{"id":"google/gemini-2.0-flash-001","context_length":1048576,"pricing":{"prompt":"0.000001","completion":"0.000002","discount":"0.5"},"architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools"],"scores":{"intelligence":85.0,"coding":80.0,"agentic":70.0},"top_provider":{"context_length":1048576,"max_completion_tokens":8192},"description":"Fast multimodal model"},{"id":"meta-llama/llama-3.3-70b","context_length":131072,"pricing":{"prompt":"0.0000005","completion":"0.0000008"},"architecture":{"input_modalities":["text"]},"supported_parameters":[],"top_provider":{"context_length":131072,"max_completion_tokens":4096},"description":null}]}""";

  private const string GeminiEndpointsJson =
      /*lang=json,strict*/
      """[{"provider_name":"Google","context_length":1048576,"max_completion_tokens":8192,"pricing":{"prompt":"0.000001","completion":"0.000002","discount":"0.5"}},{"provider_name":"OpenRouter","context_length":1048576,"max_completion_tokens":4096,"pricing":{"prompt":"0.0000015","completion":"0.000003"}}]""";

  private const string LlamaEndpointsJson =
      /*lang=json,strict*/
      """[{"provider_name":"Meta","context_length":131072,"max_completion_tokens":4096,"pricing":{"prompt":"0.0000005","completion":"0.0000008"}}]""";

  private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
      new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req) =>
      req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => JsonResponse(HttpStatusCode.OK, SampleModelsJson),
        "/api/v1/models/google/gemini-2.0-flash-001/endpoints" => JsonResponse(HttpStatusCode.OK, GeminiEndpointsJson),
        "/api/v1/models/meta-llama/llama-3.3-70b/endpoints" => JsonResponse(HttpStatusCode.OK, LlamaEndpointsJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
      };

  [Fact]
  public async Task GetAsync_OnSuccess_ParsesModelsIntoEntries()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync();

    Assert.True(result.IsSuccess);
    // 2 from gemini endpoints + 1 from llama endpoints = 3 entries
    Assert.Equal(3, result.Value!.Count);

    // Gemini via Google: effective price = 0.000001 * (1 - 0.5) = 0.0000005
    ModelProviderEntry geminiGoogle = Assert.Single(result.Value, e => e.ModelId == "google/gemini-2.0-flash-001" && e.ProviderName == "Google");
    Assert.Equal(0.0000005m, geminiGoogle.PromptPricePerToken);
    Assert.Equal(0.000001m, geminiGoogle.CompletionPricePerToken);
    Assert.Equal(1_048_576, geminiGoogle.ContextLength);
    Assert.Equal(8192, geminiGoogle.MaxCompletionTokens);
    Assert.True(geminiGoogle.SupportsToolUse);
    Assert.True(geminiGoogle.SupportsVision);
    Assert.Equal(85.0, geminiGoogle.IntelligenceScore);
    Assert.Equal(80.0, geminiGoogle.CodingScore);
    Assert.Equal(70.0, geminiGoogle.AgenticScore);
    Assert.Equal("Fast multimodal model", geminiGoogle.Description);

    // Gemini via OpenRouter: no discount, base price
    ModelProviderEntry geminiOR = Assert.Single(result.Value, e => e.ModelId == "google/gemini-2.0-flash-001" && e.ProviderName == "OpenRouter");
    Assert.Equal(0.0000015m, geminiOR.PromptPricePerToken);
    Assert.Equal(4096, geminiOR.MaxCompletionTokens);

    // Llama via Meta
    ModelProviderEntry llama = Assert.Single(result.Value, e => e.ModelId == "meta-llama/llama-3.3-70b");
    Assert.Equal("Meta", llama.ProviderName);
    Assert.False(llama.SupportsToolUse);
    Assert.False(llama.SupportsVision);
    Assert.Null(llama.Description);
  }

  [Fact]
  public async Task GetAsync_SecondCallWithinTtl_ReturnsCached_NoSecondHttpCall()
  {
    int callCount = 0;
    FakeHttpMessageHandler handler = new(req =>
    {
      _ = Interlocked.Increment(ref callCount);
      return Task.FromResult(Handler(req));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    _ = await client.GetAsync();
    _ = await client.GetAsync();

    Assert.Equal(3, callCount); // 1 models + 2 endpoints
  }

  [Fact]
  public async Task GetAsync_AfterTtlExpiry_Refetches()
  {
    int callCount = 0;
    FakeHttpMessageHandler handler = new(req =>
    {
      _ = Interlocked.Increment(ref callCount);
      return Task.FromResult(Handler(req));
    });
    using HttpClient http = new(handler);
    OpenRouterConfiguration config = new("test-key", BaseUrl) { CatalogCacheTtl = TimeSpan.Zero };
    OpenRouterCatalogClient client = new(http, config);

    _ = await client.GetAsync();
    _ = await client.GetAsync();

    Assert.Equal(6, callCount); // 2 refreshes x 3 calls each
  }

  [Fact]
  public async Task GetAsync_HttpError_ReturnsFailure()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync();

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogUnavailable", result.Error!.Code);
  }

  [Fact]
  public async Task GetAsync_MalformedJson_ReturnsFailure()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK, "not json at all")));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync();

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogParseError", result.Error!.Code);
  }

  [Fact]
  public async Task GetAsync_EndpointsCallFails_FallsBackToTopProvider()
  {
    FakeHttpMessageHandler handler = new(req =>
    {
      if (req.RequestUri!.AbsolutePath == "/api/v1/models")
      {
        return Task.FromResult(JsonResponse(HttpStatusCode.OK, SampleModelsJson));
      }

      if (req.RequestUri!.AbsolutePath == "/api/v1/models/google/gemini-2.0-flash-001/endpoints")
      {
        return Task.FromResult(JsonResponse(HttpStatusCode.OK, GeminiEndpointsJson));
      }
      // Llama endpoints fail
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync();

    Assert.True(result.IsSuccess);
    // 2 from gemini endpoints + 1 from llama top_provider fallback
    Assert.Equal(3, result.Value!.Count);
    ModelProviderEntry llama = Assert.Single(result.Value, e => e.ModelId == "meta-llama/llama-3.3-70b");
    // top_provider didn't have a provider_name, so it falls back to "Unknown"
    Assert.NotNull(llama.ProviderName);
    Assert.Equal(131072, llama.ContextLength);
    Assert.Equal(4096, llama.MaxCompletionTokens);
  }
}
