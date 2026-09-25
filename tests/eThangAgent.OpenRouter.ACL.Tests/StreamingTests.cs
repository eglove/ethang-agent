using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// The Responses API frames its SSE stream with the event name inside each data
// frame's "type" field, and terminates with data: [DONE].
public class StreamingTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  [Fact]
  public async Task Streams_ContentDeltas_InOrder_AndAssemblesFinalContent()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        ": OPENROUTER PROCESSING\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n" +
        "\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo w\"}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"orld\"}\n\n" +
        Wire.CompletedTerminal +
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
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"a1\",\"name\":\"read\"}}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"pa\"}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"th\\\":\\\"x\\\"}\"}\n\n" +
        "data: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"call_id\":\"a2\",\"name\":\"exec\"}}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":1,\"delta\":\"{}\"}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
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
  public async Task FunctionCallArgumentsDone_ReplacesAccumulatedDeltas()
  {
    const string sse =
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"a1\",\"name\":\"read\"}}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"path\\\":\\\"stale\"}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"arguments\":\"{\\\"path\\\":\\\"final.txt\\\"}\"}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"final.txt\"}", result.Value.ToolCalls[0].Arguments);
  }

  [Fact]
  public async Task FallsBack_ToJsonParsing_WhenServerIgnoresStreamFlag()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("ok", result.Value.Content);
    Assert.Empty(deltas);
    Assert.NotNull(captured);
    Assert.Contains("\"stream\":true", captured, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Streaming_ErrorStatus_MapsLikeNonStreaming()
  {
    FakeHttpMessageHandler handler = new(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") }));
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
        Task.FromResult(Wire.Sse("data: {not-json\n\ndata: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Invalid provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ArgumentsDelta_BeforeItsItemAdded_Yields_MalformedStream()
  {
    const string sse =
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{}\"}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Malformed provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FinishReason_Length_IsSurfaced_FromIncompleteTerminal()
  {
    const string sse =
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial ans\"}\n\n" +
        "data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"output\":[]}}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Length, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_ToolCalls_OutranksCompletedStatus()
  {
    const string sse =
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"a1\",\"name\":\"read\"}}\n\n" +
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{}\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_Completed_TreatsAsStop()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"done\"}\n\n" +
        Wire.CompletedTerminal +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
  }

  [Fact]
  public async Task FinishReason_UnrecognizedStatusValue_MapsToUnknown()
  {
    const string sse =
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"weird_reason\",\"output\":[]}}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Unknown, result.Value.FinishReason);
  }

  [Fact]
  public async Task JsonFallback_SurfacesFinishReason()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK,
                             /*lang=json,strict*/
                             """{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")));
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
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"cut off mid-sen\"}\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code);
  }
}
