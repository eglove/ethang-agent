using System.Net;
using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

// Reasoning frames routinely carry structural no-op siblings ("content": "" next to a
// populated reasoning_content field, and vice versa). An empty fragment carries no
// information, so it must never reach the stream observers: frontends treat every
// content delta as a stream-block switch, so an empty one shatters the open reasoning
// entry into one component per chunk.
public class StreamingFragmentTests
{
    private static readonly Uri BaseUrl = new("https://openrouter.test");
    private static ModelConfig Model => ModelConfig.Create("m", 256, 0.7f).Value!;

    private static HttpResponseMessage Sse(string raw) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(raw, System.Text.Encoding.UTF8, "text/event-stream")
        };

    [Fact]
    public async Task Empty_Fragments_Are_Suppressed()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\",\"content\":\"\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\",\"content\":\"more\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(Sse(sse)));
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http, new OpenRouterConfiguration("test-key", BaseUrl));

        var content = new List<string>();
        var reasoning = new List<string>();
        var result = await provider.SendStreamingAsync(Model, new ModelRequest([]), content.Add, reasoning.Add);

        Assert.True(result.IsSuccess);
        Assert.Equal(["think"], reasoning);
        Assert.Equal(["more"], content);
    }
}
