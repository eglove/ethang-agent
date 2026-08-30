using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class UsageWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  private static HttpResponseMessage Sse(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };

  private static HttpResponseMessage JsonBody(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  [Fact]
  public async Task Streaming_UsageFrame_PopulatesUsage_LastFrameWins()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"He\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":10}}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":110,\"completion_tokens\":12,\"prompt_tokens_details\":{\"cached_tokens\":64}}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(110, result.Value.Usage.Value.InputTokens);
    Assert.Equal(12, result.Value.Usage.Value.OutputTokens);
    Assert.Equal(64, result.Value.Usage.Value.CachedInputTokens);
  }

  [Fact]
  public async Task Streaming_WithoutUsageFrame_UsageStaysNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage);
  }

  [Fact]
  public async Task Streaming_Request_CarriesStreamOptionsIncludeUsage()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Sse("data: [DONE]\n\n");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.NotNull(captured);
    Assert.Contains("\"include_usage\":true", captured, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonStreaming_UsageObject_PopulatesUsage()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(JsonBody(/*lang=json,strict*/
        """{"choices":[{"message":{"content":"plain"}}],"usage":{"prompt_tokens":42,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":9}}}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(42, result.Value.Usage.Value.InputTokens);
    Assert.Equal(7, result.Value.Usage.Value.OutputTokens);
    Assert.Equal(9, result.Value.Usage.Value.CachedInputTokens);
  }

  [Fact]
  public async Task NonStreaming_WithoutUsage_UsageStaysNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(JsonBody(/*lang=json,strict*/
        """{"choices":[{"message":{"content":"plain"}}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage);
  }
}