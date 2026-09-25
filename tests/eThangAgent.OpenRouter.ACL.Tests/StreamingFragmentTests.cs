using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// Reasoning and content delta events routinely carry empty "delta" strings between
// their populated siblings. An empty fragment carries no information, so it must
// never reach the stream observers: frontends treat every content delta as a
// stream-block switch, so an empty one shatters the open reasoning entry into one
// component per chunk.
public class StreamingFragmentTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static ModelConfig Model => ModelConfig.Create("m", null, 256, 0.7f, 4096).Value!;

  [Fact]
  public async Task Empty_Fragments_Are_Suppressed()
  {
    string sse =
        "data: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"think\"}\n\n" +
        "data: {\"type\":\"response.reasoning_text.delta\",\"delta\":\"\"}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"\"}\n\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"more\"}\n\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n" +
        "data: [DONE]\n\n";
    FakeHttpMessageHandler handler = new(_ => Task.FromResult(Wire.Sse(sse)));
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, new OpenRouterConfiguration("test-key", BaseUrl));

    List<string> content = [];
    List<string> reasoning = [];
    Result<ModelResponse> result = await provider.SendStreamingAsync(Model, new ModelRequest([]), content.Add, reasoning.Add, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(["think"], reasoning);
    Assert.Equal(["more"], content);
  }
}
