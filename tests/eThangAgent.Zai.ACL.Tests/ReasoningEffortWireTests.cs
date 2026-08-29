using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;



#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class ReasoningEffortWireTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);

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
  public async Task Effort_MapsToWireValue(ReasoningEffort effort, string wire)
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    ZaiModelProvider provider = new(new HttpClient(handler), Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 64, 0.7f, effort).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.Equal(wire, doc.RootElement.GetProperty("reasoning_effort").GetString());
  }

  [Fact]
  public async Task Effort_Unset_SendsNoReasoningEffort()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Ok();
    });
    ZaiModelProvider provider = new(new HttpClient(handler), Config);

    _ = await provider.SendAsync(
        ModelConfig.Create("glm-5.3", null, 64, 0.7f).Value!,
        new ModelRequest([UserMsg("hi")]), TestContext.Current.CancellationToken);

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    Assert.False(doc.RootElement.TryGetProperty("thinking", out _));
  }
}
