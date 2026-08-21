using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.OpenRouter.ACL.Tests;

public class SystemPromptTests
{
    [Fact]
    public async Task SendAsync_WithSystemPrompt_PrefixesSystemMessage()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http,
            new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

        var msg = new Message(Role.User, "hi", DateTimeOffset.UtcNow);
        var result = await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([msg], SystemPrompt: "you are exec-guide"));

        Assert.True(result.IsSuccess);
        Assert.Contains("\"role\":\"system\"", capturedBody);
        var systemIndex = capturedBody!.IndexOf("\"role\":\"system\"", StringComparison.Ordinal);
        var userIndex = capturedBody.IndexOf("\"role\":\"user\"", StringComparison.Ordinal);
        Assert.True(systemIndex >= 0 && userIndex >= 0 && systemIndex < userIndex,
            "system message must precede the user message");
    }

    [Fact]
    public async Task SendAsync_WithoutSystemPrompt_HasNoSystemMessage()
    {
        string? capturedBody = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var provider = new OpenRouterModelProvider(http,
            new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

        await provider.SendAsync(
            ModelConfig.Create("m", 100, 0.5f).Value!,
            new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]));

        Assert.DoesNotContain("\"role\":\"system\"", capturedBody);
    }
}
