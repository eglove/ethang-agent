using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// The Responses API's system channel: a per-request system prompt travels as the
// body's top-level "instructions" string, never as a system-role message item.
public class SystemPromptTests
{
  private static async Task<string> CaptureBodyAsync(ModelRequest request)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));

    Result<ModelResponse> result = await provider.SendAsync(
        ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!,
        request, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    return capturedBody!;
  }

  [Fact]
  public async Task SendAsync_WithSystemPrompt_RidesInstructionsKeyBeforeInputItems()
  {
    Message msg = new(Role.User, "hi", DateTimeOffset.UtcNow);

    string body = await CaptureBodyAsync(new ModelRequest([msg], SystemPrompt: "you are exec-guide")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal("you are exec-guide", doc.RootElement.GetProperty("instructions").GetString());
    JsonElement input = doc.RootElement.GetProperty("input");
    JsonElement first = input[0];
    Assert.Equal("user", first.GetProperty("role").GetString());
    Assert.Equal("hi", first.GetProperty("content")[0].GetProperty("text").GetString());
  }

  [Fact]
  public async Task SendAsync_WithoutSystemPrompt_SendsNoInstructionsKey()
  {
    Message msg = new(Role.User, "hi", DateTimeOffset.UtcNow);

    string body = await CaptureBodyAsync(new ModelRequest([msg])).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("instructions", out _));
  }

  [Fact]
  public async Task SendAsync_WithBlankSystemPrompt_SendsNoInstructionsKey()
  {
    Message msg = new(Role.User, "hi", DateTimeOffset.UtcNow);

    string body = await CaptureBodyAsync(new ModelRequest([msg], SystemPrompt: "   ")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("instructions", out _));
  }
}
