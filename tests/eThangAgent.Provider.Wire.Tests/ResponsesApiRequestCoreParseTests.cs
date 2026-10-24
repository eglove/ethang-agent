using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

// Pins for the non-streaming response parser (ResponsesApiRequestCore.ParseResponse /
// .ParseUsage): content assembles from message items' output_text parts, function_call
// items become ToolCallRequests keyed by call_id, the body status (with its
// incomplete_details reason) maps to the finish reason — tool calls outrank any
// status — and usage maps into TokenUsage or null.
public class ResponsesApiRequestCoreParseTests
{
  [Fact]
  public void ParseResponse_MessageOutput_AssemblesOutputText_AndStops()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Hel"},{"type":"output_text","text":"lo"}]}]}""");

    Assert.True(result.IsSuccess);
    Assert.Equal("Hello", result.Value.Content);
    Assert.Empty(result.Value.ToolCalls);
    Assert.Equal(FinishReason.Stop, result.Value.FinishReason);
  }

  [Fact]
  public void ParseResponse_FunctionCallOutput_MapsCallIdAsId_AndFinishesToolCalls()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"function_call","call_id":"call_1","name":"get_time","arguments":"{\"city\":1}"}]}""");

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Content);
    _ = Assert.Single(result.Value.ToolCalls);
    Assert.Equal("call_1", result.Value.ToolCalls[0].Id);
    Assert.Equal("get_time", result.Value.ToolCalls[0].Name);
    Assert.Equal(/*lang=json,strict*/"""{"city":1}""", result.Value.ToolCalls[0].Arguments);
    Assert.Equal(FinishReason.ToolCalls, result.Value.FinishReason);
  }

  [Fact]
  public void ParseResponse_IncompleteMaxOutputTokens_MapsLength()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""");

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Length, result.Value.FinishReason);
  }

  [Fact]
  public void ParseResponse_IncompleteContentFilter_MapsContentFilter()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}""");

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.ContentFilter, result.Value.FinishReason);
  }

  [Fact]
  public void ParseResponse_IncompleteUnknownReason_MapsUnknown()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"incomplete","incomplete_details":{"reason":"something_else"},"output":[]}""");

    Assert.True(result.IsSuccess);
    Assert.Equal(FinishReason.Unknown, result.Value.FinishReason);
  }

  [Fact]
  public void ParseUsage_FullUsageObject_MapsInputOutputAndCachedTokens()
  {
    JsonDocument body = JsonDocument.Parse(
        /*lang=json,strict*/"""{"usage":{"input_tokens":100,"output_tokens":20,"input_tokens_details":{"cached_tokens":64}}}""");

    TokenUsage? usage = ResponsesApiRequestCore.ParseUsage(body.RootElement);

    Assert.Equal(new TokenUsage(100, 20, 64), usage);
  }

  [Fact]
  public void ParseUsage_ReasoningDetails_MapsReasoningTokens()
  {
    // issue #262: output_tokens_details.reasoning_tokens is the hidden share of the
    // output bill; it rides the same nested-count helper as cached_tokens.
    JsonDocument body = JsonDocument.Parse(
        /*lang=json,strict*/"""{"usage":{"input_tokens":100,"output_tokens":20,"input_tokens_details":{"cached_tokens":64},"output_tokens_details":{"reasoning_tokens":12}}}""");

    TokenUsage? usage = ResponsesApiRequestCore.ParseUsage(body.RootElement);

    Assert.Equal(new TokenUsage(100, 20, 64, null, 12), usage);
  }

  [Fact]
  public void ParseUsage_NoReasoningDetails_ReasoningTokensIsNullNotZero()
  {
    // An unreported count is NULL, never coerced to zero: the reasoning-policy
    // measurements cannot tell a fabricated zero from a measured one.
    JsonDocument body = JsonDocument.Parse(
        /*lang=json,strict*/"""{"usage":{"input_tokens":100,"output_tokens":20}}""");

    TokenUsage? usage = ResponsesApiRequestCore.ParseUsage(body.RootElement);

    Assert.Equal(new TokenUsage(100, 20), usage);
    Assert.Null(usage!.Value.ReasoningTokens);
    Assert.Null(usage.Value.CachedInputTokens);
  }

  [Fact]
  public void ParseUsage_ReportedZeroReasoning_IsDistinguishableFromAbsent()
  {
    // A provider reporting reasoning_tokens: 0 measured reasoning that billed nothing;
    // one reporting nothing measured nothing. Both facts survive parsing, so the pair
    // is asserted against each other rather than alone.
    JsonDocument reported = JsonDocument.Parse(
        /*lang=json,strict*/"""{"usage":{"input_tokens":5,"output_tokens":0,"output_tokens_details":{"reasoning_tokens":0}}}""");
    JsonDocument absent = JsonDocument.Parse(
        /*lang=json,strict*/"""{"usage":{"input_tokens":5,"output_tokens":0}}""");

    TokenUsage zero = Assert.IsType<TokenUsage>(
        ResponsesApiRequestCore.ParseUsage(reported.RootElement));
    TokenUsage unreported = Assert.IsType<TokenUsage>(
        ResponsesApiRequestCore.ParseUsage(absent.RootElement));

    Assert.Equal(0, zero.ReasoningTokens);
    Assert.Null(unreported.ReasoningTokens);
    Assert.NotEqual(zero, unreported);
  }

  [Fact]
  public void ParseResponse_ReasoningDetails_RideTheTerminalResponseBody_Too()
  {
    // The non-streaming path and the streaming terminal event both route through
    // ParseUsage, so a usage object with reasoning details must survive ParseResponse.
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"completed","output":[],"usage":{"input_tokens":7,"output_tokens":3,"output_tokens_details":{"reasoning_tokens":2}}}""");

    Assert.True(result.IsSuccess);
    Assert.Equal(new TokenUsage(7, 3, null, null, 2), result.Value.Usage);
  }

  [Fact]
  public void ParseResponse_MissingUsage_UsageIsNull()
  {
    Result<ModelResponse> result = Parse(
        /*lang=json,strict*/"""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"plain"}]}]}""");

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value.Usage);
  }

  private static Result<ModelResponse> Parse(string body) =>
      ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);
}
