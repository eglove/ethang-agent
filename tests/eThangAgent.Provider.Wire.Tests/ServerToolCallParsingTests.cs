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

  // Live-wire shapes verified against the real OpenRouter responses endpoint
  // (2026-09-26): OpenRouter's server-tool items do NOT use the "*_call" suffix —
  // the item type IS "openrouter:<tool>" (e.g. "openrouter:web_search"). The
  // added event carries no action; the done event carries action.query and
  // action.sources (url objects). These tests pin that exact shape.

  [Fact]
  public void ParseResponse_OpenRouterPrefixedItem_CarriesServerToolCall()
  {
    const string body = /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"openrouter:web_search","id":"st_tmp_1","status":"completed","action":{"type":"search","query":"current stable .NET version"}},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}""";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("web_search", call.Tool);
    Assert.Equal("current stable .NET version", call.Detail);
  }

  [Fact]
  public void ParseResponse_OpenRouterItem_CarriesSources()
  {
    const string body = /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"openrouter:web_search","id":"st_tmp_1","status":"completed","action":{"type":"search","query":"dotnet","sources":[{"type":"url","url":"https://dotnet.microsoft.com/x"},{"type":"url","url":"https://learn.microsoft.com/y"}]}},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}""";

    Result<ModelResponse> result = ResponsesApiRequestCore.ParseResponse(JsonDocument.Parse(body).RootElement);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal(2, call.Sources.Count);
    Assert.Equal("https://dotnet.microsoft.com/x", call.Sources[0]);
    Assert.Equal("https://learn.microsoft.com/y", call.Sources[1]);
  }

  [Fact]
  public async Task Streaming_OpenRouterItemAddedWithoutAction_RegistersCall()
  {
    // The live added event carries NO action; only the done event does. The call
    // must register on added (tool name known immediately) even without detail.
    string sse =
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"st_tmp_1\",\"type\":\"openrouter:web_search\",\"status\":\"in_progress\"}}\n\n" +
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
    Assert.Null(call.Detail);
  }

  [Fact]
  public async Task Streaming_OpenRouterItemDone_CarriesQueryAndSources()
  {
    string sse =
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"st_tmp_1\",\"type\":\"openrouter:web_search\",\"status\":\"in_progress\"}}\n\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"st_tmp_1\",\"type\":\"openrouter:web_search\",\"status\":\"completed\",\"action\":{\"type\":\"search\",\"query\":\"current stable .NET version\",\"sources\":[{\"type\":\"url\",\"url\":\"https://dotnet.microsoft.com/en-us/download\"}]}}}\n\n" +
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
    Assert.Equal("current stable .NET version", call.Detail);
    Assert.Equal("https://dotnet.microsoft.com/en-us/download", Assert.Single(call.Sources));
  }

  [Fact]
  public async Task Streaming_OpenRouterItemDoneWithoutAdded_StillSurfaces()
  {
    // Tolerance: a done event for an item we never saw added still surfaces the call.
    string sse =
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"st_tmp_2\",\"type\":\"openrouter:web_fetch\",\"status\":\"completed\",\"action\":{\"type\":\"fetch\",\"url\":\"https://example.com/page\"}}}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}}\n\n" +
        "data: [DONE]\n\n";
    using HttpResponseMessage response = new(HttpStatusCode.OK)
    {
      Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream"),
    };

    Result<ModelResponse> result = await ResponsesApiStreamCore.ReadSseStreamAsync(response, null, null, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    ServerToolCall call = Assert.Single(result.Value.ServerToolCalls);
    Assert.Equal("web_fetch", call.Tool);
    Assert.Equal("https://example.com/page", call.Detail);
  }
}
