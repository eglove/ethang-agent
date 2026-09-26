using System.Net;
using System.Text.Json;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Provider.Wire.Tests;

/// <summary>Server-side tool calls (OpenRouter web_search etc.) ride the response's
///     output array as their own item types. They execute server-side and never enter
///     the message history — previously they were dropped silently, so the user's
///     transcript showed nothing. The wire cores must surface them.</summary>
public class ServerToolCallParsingTests
{
  [Fact]
  public void ParseResponse_WebSearchCall_CarriesServerToolCall()
  {
    const string body = /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"web_search_call","id":"ws1","status":"completed","action":{"type":"search","query":"best coffee grinder"}},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}""";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("web_search", call.Tool);
    Assert.Equal("best coffee grinder", call.Detail);
  }

  [Fact]
  public void ParseResponse_NoServerToolCalls_EmptyList()
  {
    const string body = /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}]}""";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    Assert.Empty(result.Value.ServerToolCalls);
  }

  [Fact]
  public async Task Streaming_WebSearchCallItem_CarriesServerToolCall()
  {
    string sse =
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"web_search_call\",\"id\":\"ws1\",\"status\":\"in_progress\",\"action\":{\"type\":\"search\",\"query\":\"grinder\"}}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}\n\n" +
        "data: [DONE]\n\n";
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
    };

    Result<ModelResponse> result = await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("web_search", call.Tool);
    Assert.Equal("grinder", call.Detail);
  }
}
