using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

/// <summary>The OpenRouter session_id wire contract (sticky sessions / prompt caching):
///     when the neutral request carries a session id, the body carries it as the
///     top-level "session_id" field — OpenRouter uses it directly as the sticky
///     routing key. Absent id: no key on the wire (byte-identical legacy body).
///     Contract per https://openrouter.ai/docs/guides/best-practices/prompt-caching
///     #using-session_id-for-sticky-sessions: body field, max 256 characters.</summary>
public class SessionIdWireTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static Message UserMsg(string text) => new(Role.User, text, DateTimeOffset.UtcNow);

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
    OpenRouterModelProvider provider = new(http, new OpenRouterConfiguration("test-key", BaseUrl));

    _ = await provider.SendAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!,
        request, TestContext.Current.CancellationToken).ConfigureAwait(true);

    return capturedBody!;
  }

  [Fact]
  public async Task Body_WithSessionId_CarriesTopLevelSessionIdField()
  {
    string body = await CaptureBodyAsync(new ModelRequest(
        [UserMsg("hi")], SessionId: "3fa85f64-591c-4a0e-b3d8-0266a14e5a11")).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.Equal("3fa85f64-591c-4a0e-b3d8-0266a14e5a11",
        doc.RootElement.GetProperty("session_id").GetString());
  }

  [Fact]
  public async Task Body_WithoutSessionId_OmitsTheField()
  {
    string body = await CaptureBodyAsync(new ModelRequest([UserMsg("hi")])).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(body);
    Assert.False(doc.RootElement.TryGetProperty("session_id", out _));
  }

  [Fact]
  public async Task Body_SessionIdOver256Chars_IsRejectedAsInvalidRequest()
  {
    // The 256-character limit is a hard API constraint; a longer id is a named
    // failure at the boundary, never a silently truncated key sent to the wire.
    Result<ModelResponse> result = await new OpenRouterModelProvider(
        new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Wire.Ok()))),
        new OpenRouterConfiguration("test-key", BaseUrl)).SendAsync(
            ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!,
            new ModelRequest([UserMsg("hi")], SessionId: new string('x', 257)),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidRequest", result.Error.Code);
  }

  [Fact]
  public async Task Streaming_WithSessionId_CarriesTopLevelSessionIdField()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Wire.Sse(Wire.CompletedTerminal + "data: [DONE]\n\n");
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http, new OpenRouterConfiguration("test-key", BaseUrl));

    Result<ModelResponse> result = await provider.SendStreamingAsync(
        ModelConfig.Create("openai/gpt-5", null, 64, 0.7f, 4096).Value!,
        new ModelRequest([UserMsg("hi")], SessionId: "sess-123"),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    Assert.Equal("sess-123", doc.RootElement.GetProperty("session_id").GetString());
  }
}
