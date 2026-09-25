using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.OpenRouter.ACL.Tests;

// On the Responses API a tool result is a function_call_output item, and image parts
// ride that item's output as a native input-part array (input_image) — the
// chat-completions synthetic-user-message projection has no equivalent here.
public class ToolPartsWireTests
{
  [Fact]
  public async Task SendAsync_ToolMessageWithImagePart_RidesFunctionCallOutputPartsArray()
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
        config, new ModelRequest(messages), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.NotNull(capturedBody);
    using JsonDocument doc = JsonDocument.Parse(capturedBody);
    // Exactly three input items: the user message, the assistant's function_call,
    // and the tool result carrying the image natively — no synthetic user message,
    // no "role":"tool" item anywhere on this surface.
    Assert.Equal(
        /*lang=json,strict*/
        """[{"type":"message","role":"user","content":[{"type":"input_text","text":"look"}]},{"type":"function_call","call_id":"call-1","name":"read","arguments":"{}"},{"type":"function_call_output","call_id":"call-1","output":[{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}]""",
        doc.RootElement.GetProperty("input").GetRawText());
  }

  [Fact]
  public async Task SendAsync_ToolMessageWithTextOnly_SerializesFlatStringOutput()
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
    DateTimeOffset sentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);
    Message[] messages =
    [
      new(Role.Tool, "plain result", sentAt, ToolCallId: "call-1"),
    ];
    ModelConfig config = ModelConfig.Create("m", null, 100, 0.5f, 4096).Value!;

    _ = await provider.SendAsync(
        config, new ModelRequest(messages), TestContext.Current.CancellationToken).ConfigureAwait(true);

    using JsonDocument doc = JsonDocument.Parse(capturedBody!);
    JsonElement item = doc.RootElement.GetProperty("input")[0];
    Assert.Equal("function_call_output", item.GetProperty("type").GetString());
    Assert.Equal("call-1", item.GetProperty("call_id").GetString());
    Assert.Equal(JsonValueKind.String, item.GetProperty("output").ValueKind);
    Assert.Equal("plain result", item.GetProperty("output").GetString());
  }
}
