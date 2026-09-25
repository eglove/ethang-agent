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
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Ok()));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    ModelConfig config = ModelConfig.Create("openai/gpt-4o-mini", null, 256, 0.7f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendAsync(config, new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("ok", result.Value.Content);
    Assert.Empty(result.Value.ToolCalls);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
  }

  [Fact]
  public async Task SendAsync_SendsBearerTokenAndModel_ToResponsesEndpoint()
  {
    HttpRequestMessage? captured = null;
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o-mini", null, 128, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
    Assert.Equal("https://openrouter.test/api/v1/responses", captured.RequestUri!.ToString());
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
      return Task.FromResult(Wire.Ok());
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o-mini", null, 128, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.Equal("https://proxy.test/openrouter/api/v1/responses", captured!.RequestUri!.ToString());
  }

  [Fact]
  public async Task SendAsync_WhenToolsPresent_SerializesRequiredAndAdditionalProperties()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
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
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")], tools), TestContext.Current.CancellationToken);

    Assert.Contains("\"required\":[\"path\",\"startLine\",\"endLine\"]", capturedBody, StringComparison.Ordinal);
    Assert.Contains("\"additionalProperties\":false", capturedBody, StringComparison.Ordinal);
    Assert.Contains("\"minimum\":1", capturedBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_ParsesToolCallsFromFunctionCallItems()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK,
        /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"function_call","call_id":"call_1","name":"read","arguments":"{\"path\":\"test.txt\"}"}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Content);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
    Assert.Equal("read", result.Value.ToolCalls[0].Name);
    Assert.Contains("test.txt", result.Value.ToolCalls[0].Arguments, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_SendsToolResultAsFunctionCallOutput()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
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
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest(messages), TestContext.Current.CancellationToken);

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    JsonElement input = doc.RootElement.GetProperty("input");
    JsonElement toolOutput = input.EnumerateArray()
        .Single(item => item.GetProperty("type").GetString() == "function_call_output");
    Assert.Equal("call_1", toolOutput.GetProperty("call_id").GetString());
    Assert.Equal("result content", toolOutput.GetProperty("output").GetString());
  }

  [Theory]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ "{\"output\":5}")]
  [InlineData(/*lang=json,strict*/ "{\"output\":[{\"type\":\"function_call\",\"call_id\":\"call_1\"}]}")]
  [InlineData("not json")]
  public async Task SendAsync_WhenSuccessPayloadIsMalformed_ReturnsProviderError(string payload)
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(Wire.Json(HttpStatusCode.OK, payload)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
  }

  [Fact]
  public async Task SendAsync_OnRateLimit_ReturnsRateLimitedError()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") }));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

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
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderTimeout", result.Error.Code);
  }

  // The error body — OpenRouter's JSON naming the actual fault — is information for
  // whoever can act on it: the failure message must carry it (capped), never just
  // the bare status code.
  [Fact]
  public async Task SendAsync_OnClientError_IncludesResponseBodyInMessage()
  {
    const string errorBody = /*lang=json,strict*/ """{"error":{"message":"Model not allowed"}}""";
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(Wire.Json(HttpStatusCode.BadRequest, errorBody)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("OpenRouter returned HTTP 400:", result.Error.Message, StringComparison.Ordinal);
    Assert.Contains(errorBody, result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendAsync_OnClientErrorWithLongBody_TruncatesAt512Characters()
  {
    string errorBody = new('x', 600);
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(Wire.Json(HttpStatusCode.BadRequest, errorBody)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal($"OpenRouter returned HTTP 400: {new string('x', 512)}…", result.Error.Message);
  }

  [Fact]
  public async Task SendAsync_OnClientErrorWithEmptyBody_SendsBareStatusMessage()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
          Content = new StringContent("", Encoding.UTF8, "application/json"),
        }));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Equal("OpenRouter returned HTTP 400.", result.Error.Message);
  }

  [Fact]
  public async Task SendAsync_WithProvider_SendsProviderOnlyInBody()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o", "OpenAI", 128, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

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
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-4o", null, 128, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("provider", out _));
  }
}
