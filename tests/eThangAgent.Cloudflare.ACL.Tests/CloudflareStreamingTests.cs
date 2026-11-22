using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2007 // test code does not need ConfigureAwait
#pragma warning disable CA2000 // HttpClient owns the handler; test lifetime bounds it
// JSON002 fires only in the format/IDE host, which cannot see that these literals are wire
// fixtures under test - the pragma pair is the repo's named decision for hand-written shapes.
#pragma warning disable JSON002 // Probable JSON string detected

namespace eThangAgent.Cloudflare.ACL.Tests;

/// <summary>The Cloudflare chat provider's streamed path: Cloudflare's own frame vocabulary, tool
///     calls assembled across frames, and the same response a non-streaming call produces.
///     <para>THE FRAMES ARE CLOUDFLARE'S, NOT OPENAI'S: a top-level <c>response</c> string is the
///     content delta, a top-level <c>tool_calls</c> array carries the call fragments, and
///     <c>[DONE]</c> terminates the stream.</para></summary>
public class CloudflareStreamingTests
{
  private const string ModelId = "@cf/meta/llama-3.1-8b-instruct";

  private static CloudflareModelProvider Provider(FakeHttpMessageHandler handler)
      => new(new HttpClient(handler), new CloudflareConfiguration("acct-123", "token-abc"));

  private static ModelConfig Model() => ModelConfig.Create(ModelId, 256, 0.7f, 4096).Value!;

  private static ModelRequest Request() => new([]);

  /// <summary>One SSE frame carrying <paramref name="json"/> as its payload.</summary>
  /// <param name="json">The frame's payload.</param>
  private static string Frame(string json) => $"data: {json}\n\n";

  private const string Done = "data: [DONE]\n\n";

  [Fact]
  public async Task ThreeContentFragments_ReachTheCallbackInOrder_AndAssembleIntoTheText()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"response\":\"Hel\"}")
        + Frame("{\"response\":\"lo w\"}")
        + Frame("{\"response\":\"orld\"}")
        + Done);
    List<string> deltas = [];

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Equal(["Hel", "lo w", "orld"], deltas);
    Assert.Equal("Hello world", streamed.Value.Content);
    Assert.Empty(streamed.Value.ToolCalls);
  }

  [Fact]
  public async Task AStreamedTurn_AsksTheProviderToStream()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(Frame("{\"response\":\"x\"}") + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Contains("\"stream\":true", handler.Body, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AStreamedToolCall_ComesBackAsOneRequestWithItsArguments()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"tool_calls\":[{\"name\":\"read\",\"arguments\":{\"path\":\"a.txt\"}}]}")
        + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    ToolCallRequest call = Assert.Single(streamed.Value.ToolCalls);
    Assert.Equal("read", call.Name);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"a.txt\"}", call.Arguments);
    Assert.Equal(FinishReason.ToolCalls, streamed.Value.FinishReason);
  }

  [Fact]
  public async Task ToolCallArgumentsSplitAcrossFrames_AreAssembledIntoOneCall()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"read\"}}]}")
        + Frame("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"path\\\":\"}}]}")
        + Frame("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]}")
        + Frame("{\"tool_calls\":[{\"id\":null,\"function\":{\"name\":null}}]}")
        + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    ToolCallRequest call = Assert.Single(streamed.Value.ToolCalls);
    Assert.Equal("read", call.Name);
    Assert.Equal("call-1", call.Id);
    Assert.Equal(/*lang=json,strict*/ "{\"path\":\"a.txt\"}", call.Arguments);
  }

  [Fact]
  public async Task TwoStreamedToolCalls_AreReturnedInTheOrderTheModelOpenedThem()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]}")
        + Frame("{\"tool_calls\":[{\"index\":1,\"function\":{\"name\":\"exec\",\"arguments\":\"{}\"}}]}")
        + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Equal(["read", "exec"], streamed.Value.ToolCalls.Select(call => call.Name));
  }

  [Fact]
  public async Task AReasoningDelta_ReachesTheReasoningCallback_AndTheResponse()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}")
        + Frame("{\"response\":\"answer\"}")
        + Done);
    List<string> reasoning = [];

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), onReasoningDelta: reasoning.Add,
        ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Equal(["thinking"], reasoning);
    Assert.Equal("thinking", streamed.Value.Reasoning);
    Assert.Equal("answer", streamed.Value.Content);
  }

  [Fact]
  public async Task AStreamReportingUsage_ReturnsItOnTheResponse()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"response\":\"hi\",\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4}}")
        + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    TokenUsage usage = Assert.IsType<TokenUsage>(streamed.Value.Usage);
    Assert.Equal(9, usage.InputTokens);
    Assert.Equal(4, usage.OutputTokens);
  }

  [Fact]
  public async Task AStreamReportingNoUsage_LeavesUsageAbsent()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"response\":\"hi\"}") + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Null(streamed.Value.Usage);
  }

  [Fact]
  public async Task TheStreamingAndNonStreamingPaths_ReturnEqualResponses()
  {
    const string payload =
        "{\"response\":\"the answer\",\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2},"
        + "\"tool_calls\":[{\"name\":\"read\",\"arguments\":{\"path\":\"a.txt\"}}]}";
    FakeHttpMessageHandler nonStreaming = FakeHttpMessageHandler.Json(
        $$"""{"success":true,"errors":[],"result":{{payload}}}""");
    FakeHttpMessageHandler streaming = FakeHttpMessageHandler.Sse(
        Frame(payload) + Done);

    Result<ModelResponse> plain = await Provider(nonStreaming).SendAsync(
        Model(), Request(), TestContext.Current.CancellationToken);
    Result<ModelResponse> streamed = await Provider(streaming).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(plain.IsSuccess, plain.Error?.Message);
    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Equal(plain.Value.Content, streamed.Value.Content);
    Assert.Equal(plain.Value.FinishReason, streamed.Value.FinishReason);
    Assert.Equal(plain.Value.Usage, streamed.Value.Usage);
    Assert.Equal(
        plain.Value.ToolCalls.Select(call => (call.Name, call.Arguments)),
        streamed.Value.ToolCalls.Select(call => (call.Name, call.Arguments)));
  }

  [Fact]
  public async Task AServerThatIgnoresTheStreamFlag_IsReadAsAPlainBody()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Json(
        "{\"success\":true,\"errors\":[],\"result\":{\"response\":\"plain\"}}");
    List<string> deltas = [];

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.Equal("plain", streamed.Value.Content);
    Assert.Empty(deltas);
  }

  [Fact]
  public async Task AFrameThatIsNotJson_IsANamedFailure()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse("data: not-json\n\n" + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.False(streamed.IsSuccess);
    Assert.Equal("ProviderError", streamed.Error.Code);
  }

  [Fact]
  public async Task AStreamedErrorStatus_MapsLikeTheNonStreamingPath()
  {
    FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(
        HttpStatusCode.TooManyRequests)
    {
      Content = new StringContent(
          "{\"success\":false,\"errors\":[{\"message\":\"daily allocation exhausted\"}]}"),
    });

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.False(streamed.IsSuccess);
    Assert.Equal("RateLimited", streamed.Error.Code);
  }

  [Fact]
  public async Task AStreamedResponse_ReportsHowLongTheCallTook()
  {
    FakeHttpMessageHandler handler = FakeHttpMessageHandler.Sse(
        Frame("{\"response\":\"hi\"}") + Done);

    Result<ModelResponse> streamed = await Provider(handler).SendStreamingAsync(
        Model(), Request(), ct: TestContext.Current.CancellationToken);

    Assert.True(streamed.IsSuccess, streamed.Error?.Message);
    Assert.True(streamed.Value.DurationMs is not null);
    Assert.True(streamed.Value.FirstTokenMs is not null);
  }
#pragma warning restore JSON002 // Probable JSON string detected
}
