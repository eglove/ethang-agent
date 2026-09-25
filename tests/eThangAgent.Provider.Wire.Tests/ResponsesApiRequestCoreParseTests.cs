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
