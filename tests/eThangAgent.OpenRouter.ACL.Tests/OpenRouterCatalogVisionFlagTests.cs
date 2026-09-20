using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The vision capability flag flows from the catalog's architecture
///     input_modalities: a modality list containing image resolves true; text-only or
///     a MISSING architecture object resolves false (absence is an answer, never an
///     error). The openrouter/auto routing pseudo-model has no fetched row - its
///     capability facts are curated on the client and image input resolves true.
///     Pinned against fake fixtures.</summary>
public class OpenRouterCatalogVisionFlagTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  // One model WITH image in input_modalities, one text-only, one with NO
  // architecture object at all - every branch of the rule in one fixture.
  private const string ModelsJson =
      /*lang=json,strict*/
      """
      {"data":
      [{"id":"vision/model","context_length":65536,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools"],"top_provider":{"context_length":65536,"max_completion_tokens":8192}},
      {"id":"text/model","context_length":32768,"pricing":{"prompt":"0.000001","completion":"0.000002"},"architecture":{"input_modalities":["text"]},"supported_parameters":[],"top_provider":{"context_length":32768,"max_completion_tokens":4096}},
      {"id":"noarch/model","context_length":16384,"pricing":{"prompt":"0.000001","completion":"0.000002"},"supported_parameters":[],"top_provider":{"context_length":16384,"max_completion_tokens":2048}}]}
      """;

  private const string EndpointsJson =
      /*lang=json,strict*/
      """
      [{"provider_name":"Up","context_length":65536,"max_completion_tokens":8192,"pricing":{"prompt":"0.000001","completion":"0.000002"}}]
      """;

  private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
      new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Handler(HttpRequestMessage req) =>
      req.RequestUri!.AbsolutePath switch
      {
        "/api/v1/models" => JsonResponse(HttpStatusCode.OK, ModelsJson),
        "/api/v1/models/vision/model/endpoints" => JsonResponse(HttpStatusCode.OK, EndpointsJson),
        "/api/v1/models/text/model/endpoints" => JsonResponse(HttpStatusCode.OK, EndpointsJson),
        "/api/v1/models/noarch/model/endpoints" => JsonResponse(HttpStatusCode.OK, EndpointsJson),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound)
      };

  [Fact]
  public async Task GetAsync_ImageInInputModalities_ResolveTrue()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry vision = Assert.Single(result.Value, e => e.ModelId == "vision/model");
    Assert.True(vision.SupportsVision);
  }

  [Fact]
  public async Task GetAsync_TextOnlyModalities_ResolveFalse()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry text = Assert.Single(result.Value, e => e.ModelId == "text/model");
    Assert.False(text.SupportsVision);
  }

  [Fact]
  public async Task GetAsync_MissingArchitectureObject_ResolvesFalse_NotAnError()
  {
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess); // absence must not fail the catalog
    ModelProviderEntry noarch = Assert.Single(result.Value, e => e.ModelId == "noarch/model");
    Assert.False(noarch.SupportsVision);
  }

  [Fact]
  public async Task RoutingPseudoModel_ResolvableThroughGetAsync_AcceptsImageInput()
  {
    // openrouter/auto routes server-side across upstreams and never appears in the
    // fetched catalog: GetAsync must still serve it as a consultable entry, or a
    // resolver resolving the fallback id cannot see its image capability.
    FakeHttpMessageHandler handler = new(req => Task.FromResult(Handler(req)));
    using HttpClient http = new(handler);
    OpenRouterCatalogClient client = new(http, Config);

    Result<IReadOnlyList<ModelProviderEntry>> result = await client.GetAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ModelProviderEntry auto = Assert.Single(result.Value, e => e.ModelId == "openrouter/auto");
    Assert.True(auto.SupportsVision);
  }
}
