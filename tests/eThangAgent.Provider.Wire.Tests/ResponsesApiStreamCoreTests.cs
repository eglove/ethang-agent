using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

// Pins for the SSE stream reader (ResponsesApiStreamCore.ReadSseStreamAsync): typed
// events feed the content / reasoning sinks, function-call arguments accumulate across
// deltas with the done event authoritative, the terminal response carries usage and
// the finish reason, and the error contract holds — JsonException → "Invalid provider
// stream", structural faults → "Malformed provider stream", a stream cut off before
// [DONE] → "StreamInterrupted". The payload's "type" field is authoritative: event:
// lines and ':' keep-alive comments are ignored.
public class ResponsesApiStreamCoreTests
{
  [Fact]
  public async Task ReadSseStreamAsync_TextDeltas_AssemblesContent_AndStops()
  {
    List<string> deltas = [];

    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_text.delta","delta":"Hel"}

        data: {"type":"response.output_text.delta","delta":"lo"}

        data: [DONE]

        """,
        onContent: deltas.Add);

    Assert.True(result.IsSuccess);
    Assert.Equal("Hello", result.Value.Content);
    Assert.Equal(["Hel", "lo"], deltas);
    Assert.Empty(result.Value.ToolCalls);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
    Assert.Null(result.Value.Usage);
  }

  [Fact]
  public async Task ReadSseStreamAsync_ReasoningDeltas_FeedReasoningSink_LeaveContentNull()
  {
    List<string> reasoning = [];

    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.reasoning_text.delta","delta":"think"}

        data: {"type":"response.reasoning_summary_text.delta","delta":"ing"}

        data: [DONE]

        """,
        onReasoning: reasoning.Add);

    Assert.True(result.IsSuccess);
    Assert.Equal(["think", "ing"], reasoning);
    Assert.Null(result.Value.Content);
  }

  [Fact]
  public async Task ReadSseStreamAsync_FunctionCallStream_DoneArgumentsAreAuthoritative()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","call_id":"call_1","name":"get_time","arguments":""}}

        data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"ci"}

        data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"ty\":2}"}

        data: {"type":"response.function_call_arguments.done","output_index":0,"arguments":"{\"city\":1}"}

        data: {"type":"response.completed","response":{"status":"completed"}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
    Assert.Equal("get_time", result.Value.ToolCalls[0].Name);
    Assert.Equal(/*lang=json,strict*/"""{"city":1}""", result.Value.ToolCalls[0].Arguments);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
    Assert.Null(result.Value.Content);
  }

  [Fact]
  public async Task ReadSseStreamAsync_ArgumentsDeltaWithoutOutputItemAdded_FailsMalformedStream()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{}"}

        data: [DONE]

        """);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Malformed provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReadSseStreamAsync_StreamEndingWithoutDone_FailsStreamInterrupted()
  {
    Result<ModelResponse> result = await ReadAsync(
        """"
        data: {"type":"response.output_text.delta","delta":"partial"}

        """");

    Assert.False(result.IsSuccess);
    Assert.Equal("StreamInterrupted", result.Error.Code);
  }

  [Fact]
  public async Task ReadSseStreamAsync_CompletedTerminal_ParsesUsage()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":12,"output_tokens":34}}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal(new TokenUsage(12, 34), result.Value.Usage);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
  }

  [Fact]
  public async Task ReadSseStreamAsync_IncompleteTerminal_MapsLengthFinish()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.incomplete","response":{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"}}}

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Length, result.Value.FinishReason);
  }

  [Fact]
  public async Task ReadSseStreamAsync_ContentPartDelta_RoutesByPartType()
  {
    List<string> reasoning = [];

    Result<ModelResponse> result = await ReadAsync(
        """
        data: {"type":"response.content_part.delta","part":{"type":"output_text"},"delta":"x"}

        data: {"type":"response.content_part.delta","part":{"type":"reasoning_text"},"delta":"y"}

        data: [DONE]

        """,
        onReasoning: reasoning.Add);

    Assert.True(result.IsSuccess);
    Assert.Equal("x", result.Value.Content);
    Assert.Equal(["y"], reasoning);
  }

  [Fact]
  public async Task ReadSseStreamAsync_EventLinesAndKeepAliveComments_AreIgnored()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        : keep-alive
        event: response.output_text.delta
        data: {"type":"response.output_text.delta","delta":"Hi"}

        event: something.else

        data: [DONE]

        """);

    Assert.True(result.IsSuccess);
    Assert.Equal("Hi", result.Value.Content);
  }

  [Fact]
  public async Task ReadSseStreamAsync_MalformedJsonFrame_FailsInvalidStream()
  {
    Result<ModelResponse> result = await ReadAsync(
        """
        data: {not-json

        data: [DONE]

        """);

    Assert.False(result.IsSuccess);
    Assert.Equal("ProviderError", result.Error.Code);
    Assert.Contains("Invalid provider stream", result.Error.Message, StringComparison.Ordinal);
  }

  private static async Task<Result<ModelResponse>> ReadAsync(string sse,
      Action<string>? onContent = null, Action<string>? onReasoning = null)
  {
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    };
    return await ResponsesApiStreamCore.ReadSseStreamAsync(response, onContent, onReasoning,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
  }
}
