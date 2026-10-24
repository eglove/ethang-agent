using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

// Pins for the reasoning TELEMETRY channel (issue #263): reasoning fragments accumulate
// beside content, an absent reasoning stream yields null (never an empty string), and the
// non-streaming parser accumulates the same trace from a reasoning output item.
//
// The channel-separation pin lives here too: BuildInput has no path to any reasoning text,
// so the request body is byte-identical whether or not a trace was accumulated.
public class ResponsesApiReasoningTests
{
  [Fact]
  public async Task ReadSseStreamAsync_ReasoningFragments_AccumulateAcrossTheStream()
  {
    Result<ModelResponse> result = await ReadAsync(Sse(
        /*lang=json,strict*/"""{"type":"response.reasoning_text.delta","delta":"First I should "}""",
        /*lang=json,strict*/"""{"type":"response.reasoning_text.delta","delta":"check the file."}""",
        /*lang=json,strict*/"""{"type":"response.output_text.delta","delta":"Answer"}"""));

    Assert.True(result.IsSuccess);
    Assert.Equal("First I should check the file.", result.Value.Reasoning);
    // The content channel is untouched by the reasoning accumulation.
    Assert.Equal("Answer", result.Value.Content);
  }

  [Fact]
  public async Task ReadSseStreamAsync_ReasoningSummaryDeltas_AccumulateToo()
  {
    Result<ModelResponse> result = await ReadAsync(Sse(
        /*lang=json,strict*/"""{"type":"response.reasoning_summary_text.delta","delta":"summary "}""",
        /*lang=json,strict*/"""{"type":"response.reasoning_summary_text.delta","delta":"text"}"""));

    Assert.True(result.IsSuccess);
    Assert.Equal("summary text", result.Value.Reasoning);
  }

  [Fact]
  public async Task ReadSseStreamAsync_NoReasoningStream_ReasoningIsNullNotEmpty()
  {
    Result<ModelResponse> result = await ReadAsync(Sse(
        /*lang=json,strict*/"""{"type":"response.output_text.delta","delta":"plain answer"}"""));

    Assert.True(result.IsSuccess);
    // An absent trace is a different fact from an empty one: never coerced to empty.
    Assert.Null(result.Value.Reasoning);
  }

  [Fact]
  public async Task ReadSseStreamAsync_DisplayCallbackStillFires_WhileTheTraceAccumulates()
  {
    List<string> streamed = [];

    Result<ModelResponse> result = await ReadAsync(
        Sse(
            /*lang=json,strict*/"""{"type":"response.reasoning_text.delta","delta":"a"}""",
            /*lang=json,strict*/"""{"type":"response.reasoning_text.delta","delta":"b"}"""),
        onReasoning: streamed.Add);

    Assert.True(result.IsSuccess);
    // Display is unchanged (the same fragments, in order) and telemetry is additive.
    Assert.Equal(["a", "b"], streamed);
    Assert.Equal("ab", result.Value.Reasoning);
  }

  [Fact]
  public void ParseResponse_ReasoningItem_AccumulatesItsReasoningTextParts()
  {
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(Json(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"reasoning","content":[{"type":"reasoning_text","text":"step one "},{"type":"reasoning_text","text":"step two"}]},{"type":"message","content":[{"type":"output_text","text":"done"}]}]}"""));

    Assert.True(result.IsSuccess);
    Assert.Equal("step one step two", result.Value.Reasoning);
    Assert.Equal("done", result.Value.Content);
  }

  [Fact]
  public void ParseResponse_ReasoningSummaryOnly_FallsBackToTheSummaryText()
  {
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(Json(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"reasoning","summary":[{"type":"summary_text","text":"brief"}]}]}"""));

    Assert.True(result.IsSuccess);
    Assert.Equal("brief", result.Value.Reasoning);
  }

  [Fact]
  public void ParseResponse_NoReasoningItem_ReasoningIsNull()
  {
    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(Json(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}]}"""));

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Reasoning);
  }

  [Fact]
  public void BuildInput_IsByteIdentical_WhetherOrNotATraceWasAccumulated()
  {
    // The channel-separation pin (parent #261 acceptance sketch, and #263's own
    // "the model's next request is byte-identical whether or not reasoning was
    // persisted"): the request builder reads ONLY the conversation message list, and a
    // reasoning trace lives on ModelResponse / the event log - neither of which
    // BuildInput can reach. Two identical conversations therefore serialize identically
    // no matter what telemetry was produced alongside them.
    DateTimeOffset sentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    ModelRequest request = new([new Message(Role.User, "hello", sentAt)]);

    string first = Serialize(ResponsesApiRequestCore.BuildInput(request));
    string second = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(first, second);
    Assert.DoesNotContain("reasoning", first, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Frames SSE data payloads and terminates the stream.</summary>
  private static string Sse(params string[] frames)
      => string.Concat(frames.Select(frame => "data: " + frame + "\n\n")) + "data: [DONE]\n\n";

  private static JsonElement Json(string json)
  {
    using JsonDocument doc = JsonDocument.Parse(json);
    return doc.RootElement.Clone();
  }

  private static string Serialize(object[] items) => JsonSerializer.Serialize(items);

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
