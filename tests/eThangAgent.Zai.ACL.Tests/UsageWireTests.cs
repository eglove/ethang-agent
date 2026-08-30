using System.Net;
using System.Text;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class UsageWireTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);
  private static ModelConfig Model => ModelConfig.Create("glm-5.3", null, 256, 0.7f, 1_000_000).Value!;

  private static HttpResponseMessage Sse(string raw) =>
      new(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/event-stream") };

  private static HttpResponseMessage JsonBody(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  [Fact]
  public async Task Streaming_FinalUsageFrame_PopulatesUsage()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"ans\"}}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":90,\"completion_tokens\":8,\"prompt_tokens_details\":{\"cached_tokens\":0}}}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(90, result.Value.Usage.Value.InputTokens);
    Assert.Equal(8, result.Value.Usage.Value.OutputTokens);
    Assert.Equal(0, result.Value.Usage.Value.CachedInputTokens);
  }

  [Fact]
  public async Task Streaming_WithoutUsageFrame_UsageStaysNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Sse(
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
        "data: [DONE]\n\n")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.False(result.Value.Usage.HasValue);
  }

  [Fact]
  public async Task NonStreaming_UsageObject_PopulatesUsage()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(JsonBody(/*lang=json,strict*/
        """{"choices":[{"message":{"content":"plain"}}],"usage":{"prompt_tokens":55,"completion_tokens":6}}""")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(result.Value.Usage.HasValue);
    Assert.Equal(55, result.Value.Usage.Value.InputTokens);
    Assert.Equal(6, result.Value.Usage.Value.OutputTokens);
    Assert.False(result.Value.Usage.Value.CachedInputTokens.HasValue);
  }

  [Fact]
  public async Task NonStreaming_WithoutUsage_UsageStaysNull()
  {
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(JsonBody(/*lang=json,strict*/
        """{"choices":[{"message":{"content":"plain"}}]}""")));
    using HttpClient http = new(handler);
    ZaiModelProvider provider = new(http, Config);

    Result<ModelResponse> result = await provider.SendAsync(Model, new ModelRequest([]), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.False(result.Value.Usage.HasValue);
  }
}