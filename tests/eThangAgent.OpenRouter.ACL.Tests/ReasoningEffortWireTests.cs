using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

public class ReasoningEffortWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");
  private static OpenRouterConfiguration Config => new("test-key", BaseUrl);

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

  private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
  {
    Content = new StringContent(
        /*lang=json,strict*/
        """{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
  };

  [Theory]
  [InlineData(ReasoningEffort.Max, "max")]
  [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
  [InlineData(ReasoningEffort.High, "high")]
  [InlineData(ReasoningEffort.Medium, "medium")]
  [InlineData(ReasoningEffort.Low, "low")]
  [InlineData(ReasoningEffort.Minimal, "minimal")]
  [InlineData(ReasoningEffort.None, "none")]
  public async Task Effort_MapsToUnifiedReasoningEffort(ReasoningEffort effort, string wire)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, effort).Value!,
        new ModelRequest([UserMsg("hi")]));

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.Equal(wire, doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
  }

  [Fact]
  public async Task Effort_Unset_SendsNoReasoningObject()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]));

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("reasoning", out _));
  }
}
