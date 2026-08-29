using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class ZaiModelProviderTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  [Fact]
  public async Task SendAsync_OnSuccess_ReturnsContent()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                 /*lang=json,strict*/
                                 """{"choices":[{"message":{"content":"Hello back"}}]}""")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);
    ModelConfig config = ModelConfig.Create("glm-5.3", null, 256, 0.7f).Value!;

    Result<ModelResponse> result = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal("Hello back", result.Value.Content);
    Assert.Empty(result.Value.ToolCalls);
  }

  [Fact]
  public async Task SendAsync_SendsBearerTokenAndModel()
  {
    HttpRequestMessage? captured = null;
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonResponse(HttpStatusCode.OK,
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"ok"}}]}""");
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
    // CodingPlan is the default mode: chat goes through the coding endpoint.
    Assert.Equal("https://zai.test/coding/paas/v4/chat/completions", captured.RequestUri!.ToString());
    Assert.Contains("glm-5.3", capturedBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_InGeneralApiMode_UsesTheGeneralEndpoint()
  {
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                       /*lang=json,strict*/
                                       """{"choices":[{"message":{"content":"ok"}}]}"""));
    });
    using HttpClient http = new(handler);
    ZaiConfiguration config = new("test-key", BaseUrl) { EndpointMode = ZaiEndpointMode.GeneralApi };
    ZaiModelProvider provider = new(http, config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal("https://zai.test/paas/v4/chat/completions", captured!.RequestUri!.ToString());
  }

  [Fact]
  public async Task SendAsync_WithApiRootBase_KeepsTheBasePathSegment()
  {
    // Regression (real-API HTTP 404): the default base https://api.z.ai/api carries a
    // path segment. new Uri(base, "/paas/v4/…") treats a leading slash as host-root-
    // absolute and REPLACES the base path, posting to https://api.z.ai/paas/… — which
    // 404s. Endpoints must append to the base path instead.
    ZaiConfiguration config = new("test-key", new Uri(ZaiConfiguration.DefaultBaseUrl));
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                       /*lang=json,strict*/
                                       """{"choices":[{"message":{"content":"ok"}}]}"""));
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3-flash", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    // Default CodingPlan mode: the coding segment is appended after the base path.
    Assert.Equal("https://api.z.ai/api/coding/paas/v4/chat/completions", captured!.RequestUri!.ToString());
  }

  [Fact]
  public async Task SendAsync_GeneralApiMode_WithApiRootBase_KeepsTheBasePathSegment()
  {
    ZaiConfiguration config = new("test-key", new Uri(ZaiConfiguration.DefaultBaseUrl))
    {
      EndpointMode = ZaiEndpointMode.GeneralApi
    };
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                       /*lang=json,strict*/
                                       """{"choices":[{"message":{"content":"ok"}}]}"""));
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3-flash", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.Equal("https://api.z.ai/api/paas/v4/chat/completions", captured!.RequestUri!.ToString());
  }

  [Fact]
  public async Task SendAsync_NeverSendsThinkingOrReasoningControls()
  {
    // Deliberate decision: GLM defaults apply (flagships force thinking on); the ACL
    // must not inject thinking or reasoning_effort on its own.
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonResponse(HttpStatusCode.OK,
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"ok"}}]}""");
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
    Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
  }

  [Fact]
  public async Task SendAsync_WithUpstreamProviderPin_NeverSendsProviderField()
  {
    // ModelConfig.Provider is an OpenRouter upstream routing pin; z.ai is a single
    // provider and must never receive it.
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonResponse(HttpStatusCode.OK,
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"ok"}}]}""");
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", "SomeUpstream", 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }

  [Fact]
  public async Task SendAsync_WhenToolsPresent_SerializesRequiredAndAdditionalProperties()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonResponse(HttpStatusCode.OK,
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"ok"}}]}""");
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);
    List<ToolDefinition> tools =
        [
            new("read", "desc",
            [
                new ToolParameter("path", ToolParameterType.Text, "file path"),
                new ToolParameter("startLine", ToolParameterType.WholeNumber, "start", Minimum: 1),
                new ToolParameter("endLine", ToolParameterType.WholeNumber, "end", Minimum: 1),
            ])
        ];

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")], tools));

    Assert.Contains("\"required\":[\"path\",\"startLine\",\"endLine\"]", capturedBody, StringComparison.Ordinal);
    Assert.Contains("\"additionalProperties\":false", capturedBody, StringComparison.Ordinal);
    Assert.Contains("\"minimum\":1", capturedBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_ParsesToolCallsFromResponse()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                 /*lang=json,strict*/
                                 """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read","arguments":"{\"path\":\"test.txt\"}"}}]}}]}""")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Content);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
    Assert.Equal("read", result.Value.ToolCalls[0].Name);
    Assert.Contains("test.txt", result.Value.ToolCalls[0].Arguments, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_SendsToolMessageWithToolCallId()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonResponse(HttpStatusCode.OK,
                                   /*lang=json,strict*/
                                   """{"choices":[{"message":{"content":"final"}}]}""");
    });
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);
    List<Message> messages =
        [
            UserMsg("hi"),
            new(Role.Assistant, "", DateTimeOffset.UtcNow,
                [new ToolCall("call_1", "read", "{}")]),
            new(Role.Tool, "result content", DateTimeOffset.UtcNow, ToolCallId: "call_1"),
        ];

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest(messages));

    Assert.Contains("\"role\":\"tool\"", capturedBody, StringComparison.Ordinal);
    Assert.Contains("\"tool_call_id\":\"call_1\"", capturedBody, StringComparison.Ordinal);
    Assert.Contains("result content", capturedBody, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ "{\"choices\":[]}")]
  [InlineData(/*lang=json,strict*/ "{\"choices\":[{}]}")]
  [InlineData(/*lang=json,strict*/ "{\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"call_1\"}]}}]}")]
  [InlineData("not json")]
  public async Task SendAsync_WhenSuccessPayloadIsMalformed_ReturnsProviderError(string payload)
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK, payload)));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
  }

  [Fact]
  public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal("RateLimited", result.Error.Code);
  }

  [Fact]
  public async Task SendAsync_OnTimeout_ReturnsProviderTimeoutError()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderTimeout", result.Error.Code);
  }

  [Theory]
  [InlineData("sensitive", FinishReason.ContentFilter)]
  [InlineData("model_context_window_exceeded", FinishReason.Length)]
  [InlineData("network_error", FinishReason.Unknown)]
  public async Task SendAsync_MapsZaiFinishReasonVocabulary(string wire, FinishReason expected)
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK,
            $$"""{"choices":[{"message":{"content":"x"},"finish_reason":"{{wire}}"}]}""")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal(expected, result.Value.FinishReason);
  }

  private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
      new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
