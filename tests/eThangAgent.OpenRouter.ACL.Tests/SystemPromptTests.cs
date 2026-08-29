using System.Net;
using System.Text;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class SystemPromptTests
{
  [Fact]
  public async Task SendAsync_WithSystemPrompt_PrefixesSystemMessage()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(/*lang=json,strict*/ """{"choices":[{"message":{"content":"ok"}}]}""",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

    Message msg = new(Role.User, "hi", DateTimeOffset.UtcNow);
    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
        new ModelRequest([msg], SystemPrompt: "you are exec-guide"));

    Assert.True(result.IsSuccess);
    Assert.Contains("\"role\":\"system\"", capturedBody, StringComparison.Ordinal);
    int systemIndex = capturedBody!.IndexOf("\"role\":\"system\"", StringComparison.Ordinal);
    int userIndex = capturedBody.IndexOf("\"role\":\"user\"", StringComparison.Ordinal);
    Assert.True(systemIndex >= 0 && userIndex >= 0 && systemIndex < userIndex,
        "system message must precede the user message");
  }

  [Fact]
  public async Task SendAsync_WithoutSystemPrompt_HasNoSystemMessage()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(/*lang=json,strict*/ """{"choices":[{"message":{"content":"ok"}}]}""",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

    _ = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f).Value!,
        new ModelRequest([new Message(Role.User, "hi", DateTimeOffset.UtcNow)]));

    Assert.DoesNotContain("\"role\":\"system\"", capturedBody, StringComparison.Ordinal);
  }
}
