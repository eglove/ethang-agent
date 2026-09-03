using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; response/handler disposal is the test host's concern
namespace eThangAgent.Local.ACL.Tests;

public class LocalModelCatalogTests
{
  private static readonly Uri BaseUrl = new("http://localhost:1234");

  private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
      new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

  /// <summary>Handler routing by endpoint path: /v1/models -> <paramref name="models"/>,
  ///     /api/v0/models -> <paramref name="lmStudio"/> (404 when absent), /api/show ->
  ///     <paramref name="ollama"/>(request body) (404 when absent). Records every request
  ///     URL and every /api/show body so tests can assert exactly what was probed.</summary>
  private static (FakeHttpMessageHandler Handler, List<HttpRequestMessage> Requests, List<string> ShowBodies)
      Recorder(HttpResponseMessage models, HttpResponseMessage? lmStudio = null,
          Func<string, HttpResponseMessage>? ollama = null)
  {
    List<HttpRequestMessage> requests = [];
    List<string> showBodies = [];
    FakeHttpMessageHandler handler = new(async req =>
    {
      requests.Add(req);
      switch (req.RequestUri!.AbsolutePath)
      {
        case "/v1/models":
          return models;
        case "/api/v0/models":
          return lmStudio ?? NotFound();
        case "/api/show" when ollama is not null:
          string body = req.Content is null
              ? string.Empty
              : await req.Content.ReadAsStringAsync().ConfigureAwait(false);
          showBodies.Add(body);
          return ollama(body);
        default:
          return NotFound();
      }
    });
    return (handler, requests, showBodies);
  }

  private static HttpResponseMessage Models(string data) => Json(HttpStatusCode.OK,
      /*lang=json,strict*/
      "{\"data\":[" + data + "]}");

  [Fact]
  public async Task GetAsync_ListsModels()
  {
    (FakeHttpMessageHandler handler, _, _) = Recorder(Models(
        "{\"id\":\"qwen2.5-coder-32b\"},{\"id\":\"llama-3.3-70b\"}"));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, result.Value.Count);
    Assert.Equal("qwen2.5-coder-32b", result.Value[0].ModelId);
    Assert.Equal("llama-3.3-70b", result.Value[1].ModelId);
    foreach (ModelProviderEntry entry in result.Value)
    {
      Assert.Equal(LocalModelCatalog.ProviderName, entry.ProviderName);
      Assert.Equal(0m, entry.PromptPricePerToken);
      Assert.Equal(0m, entry.CompletionPricePerToken);
      Assert.True(entry.SupportsToolUse);
      Assert.False(entry.SupportsVision);
      Assert.Null(entry.IntelligenceScore);
      Assert.Null(entry.CodingScore);
      Assert.Null(entry.AgenticScore);
      Assert.Null(entry.LatencyMs);
      Assert.Null(entry.ThroughputTokensPerSec);
    }
  }

  [Fact]
  public async Task GetAsync_LmStudioContextLengthWins()
  {
    (FakeHttpMessageHandler handler, List<HttpRequestMessage> requests, _) = Recorder(
        models: Models(/*lang=json,strict*/ """{"id":"qwen2.5-coder-32b"}"""),
        lmStudio: Json(HttpStatusCode.OK,
            /*lang=json,strict*/
            """{"data":[{"id":"qwen2.5-coder-32b","context_length":131072}]}"""));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(131072, result.Value[0].ContextLength);
    Assert.Equal(131072, result.Value[0].MaxCompletionTokens);
    // The batch LM Studio answer settles every listed id: the per-model Ollama probe never runs.
    Assert.DoesNotContain(requests, r => r.RequestUri!.AbsolutePath == "/api/show");
  }

  [Fact]
  public async Task GetAsync_OllamaShowFallback()
  {
    (FakeHttpMessageHandler handler, _, List<string> showBodies) = Recorder(
        models: Models(/*lang=json,strict*/ """{"id":"llama-3.3-70b"}"""),
        ollama: _ => Json(HttpStatusCode.OK,
            /*lang=json,strict*/
            """{"context_length":65536}"""));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(65536, result.Value[0].ContextLength);
    // The probe asks for exactly the listed model, by id.
    string body = Assert.Single(showBodies);
    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal("llama-3.3-70b", doc.RootElement.GetProperty("model").GetString());
  }

  [Fact]
  public async Task GetAsync_FloorWhenNoProbeAnswers()
  {
    (FakeHttpMessageHandler handler, _, _) = Recorder(models: Models(/*lang=json,strict*/ """{"id":"qwen2.5-coder-32b"}"""));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(LocalModelCatalog.DefaultContextFloor, result.Value[0].ContextLength);
    Assert.Equal(LocalModelCatalog.DefaultContextFloor, result.Value[0].MaxCompletionTokens);
  }

  [Fact]
  public async Task GetAsync_ZeroReportedContextCountsAsNoAnswer()
  {
    (FakeHttpMessageHandler handler, _, _) = Recorder(
        models: Models(/*lang=json,strict*/ """{"id":"qwen2.5-coder-32b"}"""),
        lmStudio: Json(HttpStatusCode.OK,
            /*lang=json,strict*/
            """{"data":[{"id":"qwen2.5-coder-32b","context_length":0}]}"""));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(LocalModelCatalog.DefaultContextFloor, result.Value[0].ContextLength);
  }

  [Fact]
  public async Task GetAsync_ServerDown_ProviderUnreachable()
  {
    FakeHttpMessageHandler handler =
        new(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderUnreachable", result.Error.Code);
  }

  [Fact]
  public async Task GetAsync_EmptyLineup_Fails()
  {
    (FakeHttpMessageHandler handler, _, _) = Recorder(Models(string.Empty));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> result =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderUnreachable", result.Error.Code);
    Assert.Contains("no models", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetAsync_CachesResolvedLineup()
  {
    (FakeHttpMessageHandler handler, List<HttpRequestMessage> requests, _) = Recorder(models: Models(/*lang=json,strict*/ """{"id":"qwen2.5-coder-32b"}"""));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<IReadOnlyList<ModelProviderEntry>> first =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<IReadOnlyList<ModelProviderEntry>> second =
        await catalog.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    // Snapshot at first fetch: one /v1/models request across both calls, and the second
    // caller receives the identical list instance.
    Assert.Equal(1, requests.Count(r => r.RequestUri!.AbsolutePath == "/v1/models"));
    Assert.Same(first.Value, second.Value);
  }

  [Fact]
  public async Task FirstModelIdAsync_ReturnsFirstEntryId()
  {
    (FakeHttpMessageHandler handler, _, _) = Recorder(Models(
        "{\"id\":\"qwen2.5-coder-32b\"},{\"id\":\"llama-3.3-70b\"}"));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<string> result =
        await catalog.FirstModelIdAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal("qwen2.5-coder-32b", result.Value);
  }

  [Fact]
  public async Task FirstModelIdAsync_FailsWhenServerUnreachable()
  {
    FakeHttpMessageHandler handler =
        new(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
    using HttpClient http = new(handler);
    LocalModelCatalog catalog = new(http, new LocalConfiguration(BaseUrl));

    Result<string> result =
        await catalog.FirstModelIdAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderUnreachable", result.Error.Code);
  }
}
