using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// On the Responses API, usage rides the terminal response event (streaming) and the
// response body's usage object (non-streaming), scored in input_tokens /
// output_tokens / input_tokens_details.cached_tokens. The last terminal event wins.
public class UsageWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  [Fact]
  public async Task Streaming_TerminalResponseEvent_PopulatesUsage_LastTerminalWins()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"He\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":110,\"output_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":64}}}}\n\n" +
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
  public async Task Streaming_WithoutTerminalUsage_UsageStaysNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage);
  }

  [Fact]
  public async Task Streaming_Request_CarriesStreamFlagAndNoStreamOptions()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      captured = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Sse("data: [DONE]\n\n");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.NotNull(captured);
    Assert.Contains("\"stream\":true", captured, StringComparison.Ordinal);
    // stream_options is the chat-completions include-usage mechanism; the responses
    // surface has no such key — usage arrives on the terminal event unconditionally.
    Assert.DoesNotContain("stream_options", captured, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonStreaming_UsageObject_PopulatesUsage()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}],"usage":{"input_tokens":42,"output_tokens":7,"input_tokens_details":{"cached_tokens":9}}}""")));
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
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Ok()));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage);
  }
}
