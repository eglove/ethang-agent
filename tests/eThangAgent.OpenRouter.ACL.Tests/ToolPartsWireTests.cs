using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// Spec 5.2 OpenRouter row: a tool message carrying an image part serializes the parts
// array ON the tool message itself (no projection — OpenRouter consumes vision on the
// tool result directly).
public class ToolPartsWireTests
{
  [Fact]
  public async Task SendAsync_ToolMessageWithImagePart_SerializesPartsOnTheToolMessage()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(/*lang=json,strict*/ "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                  Encoding.UTF8, "application/json")
      };
    });
    using HttpClient http = new(handler);
    OpenRouterModelProvider provider = new(http,
        new OpenRouterConfiguration("test-key", new Uri("https://openrouter.test")));
    DateTimeOffset sentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    Message[] messages =
    [
      new(Role.User, "look", sentAt),
      new(Role.Assistant, "", sentAt,
          [new ToolCall("call-1", "read", /*lang=json,strict*/ "{}")]),
      new(Role.Tool, "see attached", sentAt, ToolCallId: "call-1",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendAsync(
        config, new ModelRequest(messages), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.NotNull(capturedBody);
    using JsonDocument doc = JsonDocument.Parse(capturedBody);
    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"call-1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}],"tool_call_id":"call-1"}]""",
        doc.RootElement.GetProperty("messages").GetRawText());
  }
}
