using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class OpenRouterModelProviderTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  [Fact]
  public async Task SendAsync_OnSuccess_ReturnsContent()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                 /*lang=json,strict*/
                                 """{"choices":[{"message":{"content":"Hello back"}}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    ModelConfig config = ModelConfig.Create("openai/gpt-4o-mini", null, 256, 0.7f).Value!;

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
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o-mini", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.True(result.IsSuccess);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
    Assert.Equal("https://openrouter.test/api/v1/chat/completions", captured.RequestUri!.ToString());
    Assert.Contains("openai/gpt-4o-mini", capturedBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_WithPathBearingBase_AppendsInsteadOfReplacing()
  {
    // Regression: new Uri(base, "/api/v1/…") treats the leading slash as host-root-
    // absolute and would replace any base path segment. The endpoint must append.
    OpenRouterConfiguration config = new("test-key", new Uri("https://proxy.test/openrouter"));
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                                       /*lang=json,strict*/
                                       """{"choices":[{"message":{"content":"ok"}}]}"""));
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o-mini", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.Equal("https://proxy.test/openrouter/api/v1/chat/completions", captured!.RequestUri!.ToString());
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
    OpenRouterModelProvider provider = new(http, Config);
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
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
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
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
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
    OpenRouterModelProvider provider = new(http, Config);
    List<Message> messages =
        [
            UserMsg("hi"),
            new(Role.Assistant, "", DateTimeOffset.UtcNow,
                [new ToolCall("call_1", "read", "{}")]),
            new(Role.Tool, "result content", DateTimeOffset.UtcNow, ToolCallId: "call_1"),
        ];

    _ = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
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
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
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
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
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
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderTimeout", result.Error.Code);
  }

  private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) =>
      new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
  [Fact]
  public async Task SendAsync_WithProvider_SendsProviderOnlyInBody()
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
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o", "OpenAI", 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    Assert.Contains("provider", capturedBody!, StringComparison.Ordinal);
    Assert.Contains("only", capturedBody!, StringComparison.Ordinal);
    Assert.Contains("OpenAI", capturedBody!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_WithoutProvider_DoesNotSendProviderField()
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
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o", null, 128, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }
}
