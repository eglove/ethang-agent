using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class StreamingTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  private static HttpResponseMessage Sse(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };

  private static HttpResponseMessage JsonBody(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  [Fact]
  public async Task Streams_ContentDeltas_InOrder_AndAssemblesFinalContent()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        ": OPENROUTER PROCESSING\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"lo w\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"orld\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"total_tokens\":9}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["Hel", "lo w", "orld"], deltas);
    Assert.Equal("Hello world", result.Value.Content);
    Assert.Empty(result.Value.ToolCalls);
  }

  [Fact]
  public async Task Assembles_ToolCallFragments_ByIndex_AcrossChunks()
  {
    const string sse =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a1\",\"type\":\"function\"," +
        "\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"x\\\"}\"}}," +
        "{\"index\":1,\"id\":\"a2\",\"type\":\"function\",\"function\":{\"name\":\"exec\",\"arguments\":\"{}\"}}]}}]}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Content);
    Assert.Equal(2, result.Value.ToolCalls.Count);
    Assert.Equal("a1", result.Value.ToolCalls[0].Id);
    Assert.Equal("read", result.Value.ToolCalls[0].Name);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"x\"}", result.Value.ToolCalls[0].Arguments);
    Assert.Equal("a2", result.Value.ToolCalls[1].Id);
    Assert.Equal("exec", result.Value.ToolCalls[1].Name);
    Assert.Equal("{}", result.Value.ToolCalls[1].Arguments);
  }

  [Fact]
  public async Task FallsBack_ToJsonParsing_WhenServerIgnoresStreamFlag()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonBody(/*lang=json,strict*/ """{"choices":[{"message":{"content":"plain"}}]}""");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("plain", result.Value.Content);
    Assert.Empty(deltas);
    Assert.NotNull(captured);
    Assert.Contains("\"stream\":true", captured, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Streaming_ErrorStatus_MapsLikeNonStreaming()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("RateLimited", result.Error.Code);
  }

  [Fact]
  public async Task MalformedChunk_Yields_ProviderError()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(Sse("data: {not-json\n\ndata: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Invalid provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ToolCallFragment_WithoutFunctionName_Yields_ProviderError()
  {
    const string sse =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a1\"," +
        "\"function\":{\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Malformed provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FinishReason_Length_IsSurfaced()
  {
    const string sse =
        "data: {\"choices\":[{\"delta\":{\"content\":\"partial ans\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Length, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_ToolCalls_IsSurfaced()
  {
    const string sse =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a1\",\"type\":\"function\"," +
        "\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_Missing_TreatsAsStop()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"done\"}}]}\n\ndata: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_UnrecognizedValue_MapsToUnknown()
  {
    const string sse =
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"weird_reason\"}]}\n\ndata: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Unknown, result.Value.FinishReason);
  }

  [Fact]
  public async Task JsonFallback_SurfacesFinishReason()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(JsonBody(
                             /*lang=json,strict*/
                             """{"choices":[{"message":{"content":"plain"},"finish_reason":"length"}]}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), _ => { }, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Length, result.Value.FinishReason);
  }

  [Fact]
  public async Task StreamEndingWithoutDoneMarker_Yields_StreamInterrupted()
  {
    // A dropped connection must not masquerade as a successful (truncated)
    // completion — the pre-fix bug behind turns silently stopping mid-task.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"cut off mid-sen\"}}]}\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code);
  }
}
