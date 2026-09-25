using System.Net;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The usage.server_tool_use object (web_search_requests when present) must
///     ride the existing usage accounting: captured into TokenUsage.ServerToolCalls
///     on every parse path (streaming and non-streaming). A usage object without it
///     parses unchanged (count stays null).</summary>
public class ServerToolUsageTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  [Fact]
  public async Task NonStreaming_ServerToolUse_PopulatesServerToolCalls()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}],"usage":{"input_tokens":42,"output_tokens":7,"server_tool_use":{"web_search_requests":3}}}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(42, result.Value.Usage.Value.InputTokens);
    Assert.Equal(7, result.Value.Usage.Value.OutputTokens);
    Assert.Equal(3, result.Value.Usage.Value.ServerToolCalls);
  }

  [Fact]
  public async Task NonStreaming_WithoutServerToolUse_ServerToolCallsStayNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Json(HttpStatusCode.OK, /*lang=json,strict*/
        """{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"plain"}]}],"usage":{"input_tokens":42,"output_tokens":7}}""")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Null(result.Value.Usage.Value.ServerToolCalls);
  }

  [Fact]
  public async Task Streaming_ServerToolUse_PopulatesServerToolCalls()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"He\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"server_tool_use\":{\"web_search_requests\":5}}}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(5, result.Value.Usage.Value.ServerToolCalls);
  }
}
