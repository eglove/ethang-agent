using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Local.ACL.Tests;

public class LocalModelProviderTests
{
  private static readonly Uri BaseUrl = new("http://localhost:1234/v1");

  // One attempt by default: status-mapping cases must fail immediately, with no real
  // backoff sleeps; the retry test passes its own two-attempt policy plus a fake delay.
  private static LocalConfiguration Config(string? apiKey = null, RetryPolicy? retry = null) =>
      new(BaseUrl, apiKey) { Retry = retry ?? new RetryPolicy(1) };

  private static ModelConfig Model(ReasoningEffort? effort = null) =>
      ModelConfig.Create("local-model", null, 128, 0.7f, 4096, effort).Value!;

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

  private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
      new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private static HttpResponseMessage Ok() =>
      Json(HttpStatusCode.OK,
                           /*lang=json,strict*/
                           """{"choices":[{"message":{"content":"ok"}}]}""");

  [Fact]
  public async Task SendAsync_SendsOpenAiShape()
  {
    HttpRequestMessage? captured = null;
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req;
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());
    List<ToolDefinition> tools =
        [
            new("read", "Read a file",
            [
                new ToolParameter("path", ToolParameterType.Text, "file path"),
            ]),
        ];

    Result<ModelResponse> result = await provider.SendAsync(
        Model(ReasoningEffort.High),
        new ModelRequest([UserMsg("hi")], tools),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(HttpMethod.Post, captured!.Method);
    Assert.Equal("http://localhost:1234/v1/chat/completions", captured.RequestUri!.ToString());
    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    JsonElement root = doc.RootElement;
    Assert.Equal("local-model", root.GetProperty("model").GetString());
    Assert.Equal(JsonValueKind.Array, root.GetProperty("messages").ValueKind);
    Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
    Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
    Assert.Equal(0.7, root.GetProperty("temperature").GetDouble(), precision: 5);
    // Spec pin: local servers never receive a reasoning_effort knob, even when the
    // user picked a level in the host's effort picker.
    Assert.False(root.TryGetProperty("reasoning_effort", out _));
    // Tools are serialized when the request carries any.
    Assert.Equal(JsonValueKind.Array, root.GetProperty("tools").ValueKind);
    Assert.Equal("read", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
  }

  [Fact]
  public async Task SendAsync_WithApiKey_SendsBearerHeader()
  {
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(Ok());
    });
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config("test-key"));

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("Bearer test-key", captured!.Headers.Authorization?.ToString());
  }

  [Fact]
  public async Task SendAsync_WithoutApiKey_SendsNoAuthHeader()
  {
    HttpRequestMessage? captured = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      captured = req;
      return Task.FromResult(Ok());
    });
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.False(captured!.Headers.Contains("Authorization"));
    Assert.Null(captured.Headers.Authorization);
  }

  [Fact]
  public async Task SendAsync_ParsesContentToolCallsUsage()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(
        Json(HttpStatusCode.OK,
                         /*lang=json,strict*/
                         """{"choices":[{"message":{"content":"hi","tool_calls":[{"id":"t1","type":"function","function":{"name":"read","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":7}}""")));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("hi", result.Value.Content);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal(new ToolCallRequest("t1", "read", "{}"), result.Value.ToolCalls[0]);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
    Assert.Equal(new TokenUsage(5, 7, null), result.Value.Usage);
  }

  [Theory]
  [InlineData(400, "ProviderError", "Local server returned HTTP 400.")]
  [InlineData(429, "RateLimited", "Local server rate limit exceeded.")]
  [InlineData(408, "ProviderTimeout", "Request timed out.")]
  [InlineData(500, "ProviderError", "Local server returned HTTP 500.")]
  public async Task SendAsync_StatusMapping(int statusCode, string expectedCode, string expectedMessage)
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Status((HttpStatusCode)statusCode)));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal(expectedCode, result.Error.Code);
    Assert.Equal(expectedMessage, result.Error.Message);
  }

  [Fact]
  public async Task SendAsync_Retries5xxThenSucceeds()
  {
    int calls = 0;
    List<TimeSpan> delays = [];
    FakeHttpMessageHandler handler = new(_ =>
    {
      calls++;
      return Task.FromResult(calls == 1 ? Status(HttpStatusCode.InternalServerError) : Ok());
    });
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config(retry: new RetryPolicy(2)),
        delay: (span, _) =>
        {
          delays.Add(span);
          return Task.CompletedTask;
        },
        jitter: () => 0.0);

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, calls);
    TimeSpan delay = Assert.Single(delays);
    Assert.Equal(TimeSpan.FromMilliseconds(500), delay);
  }

  [Fact]
  public async Task SendAsync_NonJsonErrorBody_SurfacesExcerpt()
  {
    const string html = "<html><body><h1>ERROR</h1><p>The requested URL was rejected</p></body></html>";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
      Content = new StringContent(html, Encoding.UTF8, "text/html"),
    }));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendAsync(
        Model(), new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("The requested URL was rejected", result.Error.Message, StringComparison.Ordinal);
  }
}
