using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Local.ACL.Tests;

public class StreamingTests
{
  private static readonly Uri BaseUrl = new("http://localhost:1234/v1");

  // Streaming fixtures answer on the first call: one attempt, no backoff sleeps.
  private static LocalConfiguration Config(string? apiKey = null) =>
      new(BaseUrl, apiKey) { Retry = new RetryPolicy(1) };

  private static ModelConfig Model => ModelConfig.Create("local-model", null, 128, 0.7f, 4096).Value!;

  private static HttpResponseMessage Sse(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };

  private static HttpResponseMessage JsonBody(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  [Fact]
  public async Task Streams_ContentDeltas_InOrder_AndAssemblesFinalContent()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        ": keep-alive\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
        "\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"lo w\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"orld\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"total_tokens\":9}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["Hel", "lo w", "orld"], deltas);
    Assert.Equal("Hello world", result.Value.Content);
    Assert.Empty(result.Value.ToolCalls);
  }

  [Fact]
  public async Task Streams_ReasoningContentDeltas_ToReasoningCallback()
  {
    // Local reasoning arrives as reasoning_content delta frames (the field the
    // OpenAI-compatible family documents); it must reach the reasoning callback while
    // never leaking into the assembled content.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"thin\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"king\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());
    List<string> reasoningDeltas = [];
    List<string> contentDeltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]),
        contentDeltas.Add, reasoningDeltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["thin", "king"], reasoningDeltas);
    Assert.Equal(["answer"], contentDeltas);
    Assert.Equal("answer", result.Value.Content);
  }

  [Fact]
  public async Task Assembles_ToolCallFragments_ByIndex_AcrossChunks()
  {
    // One tool call whose fragments span two chunks: id/name on the first fragment,
    // argument text concatenated across both — assembled into exactly one request.
    const string sse =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a1\",\"type\":\"function\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"pa\"}}]}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"th\\\":\\\"x\\\"}\"}}]}}]}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(sse)));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Content);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal("a1", result.Value.ToolCalls[0].Id);
    Assert.Equal("read", result.Value.ToolCalls[0].Name);
    Assert.Equal(/*lang=json,strict*/ """{"path":"x"}""", result.Value.ToolCalls[0].Arguments);
  }

  [Fact]
  public async Task FinalUsageFrame_Wins()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":4}}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("ab", result.Value.Content);
    Assert.Equal(new TokenUsage(10, 20, 4), result.Value.Usage);
  }

  [Fact]
  public async Task StreamEndingWithoutDoneMarker_Yields_StreamInterrupted()
  {
    // A dropped connection must not masquerade as a successful (truncated) completion.
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"cut off mid-sen\"}}]}\n\n")));
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code);
  }

  [Fact]
  public async Task FallsBack_ToJsonParsing_WhenServerIgnoresStreamFlag()
  {
    string? captured = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      captured = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return JsonBody(
          /*lang=json,strict*/
          """{"choices":[{"message":{"content":"plain"}}]}""");
    });
    using HttpClient http = new(handler);
    LocalModelProvider provider = new(http, Config());
    List<string> deltas = [];

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), deltas.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal("plain", result.Value.Content);
    Assert.Empty(deltas);
    Assert.NotNull(captured);
    // The request DID ask for a stream — the fallback is the server's answer, not a different request.
    Assert.Contains("\"stream\":true", captured, StringComparison.Ordinal);
  }
}
