using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterCatalogClientTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private const string SampleModelsJson =
                           /*lang=json,strict*/
                           """{"data":[{"id":"google/gemini-2.0-flash-001","context_length":1048576,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools"],"description":"Fast multimodal model"},{"id":"meta-llama/llama-3.3-70b","context_length":131072,"pricing":{"prompt":"0.0000005","completion":"0.0000008"},"architecture":{"input_modalities":["text"]},"supported_parameters":[],"description":null}]}""";

  [Fact]
  public async Task GetAsync_OnSuccess_ParsesModelsIntoEntries()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK, SampleModelsJson)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelCatalogEntry>> result = await client.GetAsync();

    Assert.True(result.IsSuccess);
    Assert.Equal(2, result.Value!.Count);
    Assert.Equal("google/gemini-2.0-flash-001", result.Value[0].Id);
    Assert.Equal(0.000001m, result.Value[0].PromptPricePerToken);
    Assert.Equal(0.000002m, result.Value[0].CompletionPricePerToken);
    Assert.Equal(1_048_576, result.Value[0].ContextLength);
    Assert.True(result.Value[0].SupportsToolUse);
    Assert.True(result.Value[0].SupportsVision);
    Assert.Equal("Fast multimodal model", result.Value[0].Description);
    Assert.Equal("meta-llama/llama-3.3-70b", result.Value[1].Id);
    Assert.False(result.Value[1].SupportsToolUse);
    Assert.False(result.Value[1].SupportsVision);
    Assert.Null(result.Value[1].Description);
  }

  [Fact]
  public async Task GetAsync_SecondCallWithinTtl_ReturnsCached_NoSecondHttpCall()
  {
    int callCount = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      callCount++;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK, SampleModelsJson));
    });
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    _ = await client.GetAsync();
    _ = await client.GetAsync();

    Assert.Equal(1, callCount);
  }

  [Fact]
  public async Task GetAsync_AfterTtlExpiry_Refetches()
  {
    int callCount = 0;
    FakeHttpMessageHandler handler = new(_ =>
    {
      callCount++;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK, SampleModelsJson));
    });
    using HttpClient http = new(handler);
    OpenRouterConfiguration config = new("test-key", BaseUrl) { CatalogCacheTtl = TimeSpan.Zero };
    OpenRouterCatalogClient client = new(http, config);

    _ = await client.GetAsync();
    _ = await client.GetAsync();

    Assert.Equal(2, callCount);
  }

  [Fact]
  public async Task GetAsync_HttpError_ReturnsFailure()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelCatalogEntry>> result = await client.GetAsync();

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

    Result<IReadOnlyList<ModelCatalogEntry>> result = await client.GetAsync();

    Assert.False(result.IsSuccess);
    Assert.Equal("CatalogParseError", result.Error!.Code);
  }

  private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
  {
    HttpResponseMessage response = new(status)
    {
      Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    return response;
  }
}
