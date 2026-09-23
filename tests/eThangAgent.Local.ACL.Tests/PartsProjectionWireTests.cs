using System.Net;
using System.Text;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

#pragma warning disable CA2000 // HttpClient owns the handler; provider lifetime bounds it
namespace eThangAgent.Local.ACL.Tests;

// Spec 5.3 Local pins: the same PostTurnUser projection as z.ai — tool content
// serializes flat text and ALL image parts of the turn's tool results merge into ONE
// synthetic user message placed after the LAST tool result of the assistant
// tool_calls block, each image preceded by "[computer screenshot for tool call
// <toolCallId>]"; none when the turn has no images.
public class PartsProjectionWireTests
{
  private static readonly DateTimeOffset SentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);

  [Fact]
  public async Task SendAsync_SingleToolCallWithImage_MergesIntoSyntheticUserMessage()
  {
    string json = await CaptureMessagesAsync(
    [
      new(Role.User, "look", SentAt),
      new(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new(Role.Tool, "see attached", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ]);

    Assert.Equal(
        /*lang=json,strict*/"""[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":"see attached","tool_call_id":"c1"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c1]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]}]""",
        json);
  }

  [Fact]
  public async Task SendAsync_TwoToolCallsSecondHasImage_GoesAfterLastToolResult()
  {
    string json = await CaptureMessagesAsync(
    [
      new(Role.User, "look", SentAt),
      new(Role.Assistant, "", SentAt,
      [
        new ToolCall("c1", "read", "{}"),
        new ToolCall("c2", "screenshot", "{}"),
      ]),
      new(Role.Tool, "file body", SentAt, ToolCallId: "c1"),
      new(Role.Tool, "", SentAt, ToolCallId: "c2",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ]);

    Assert.Equal(
        /*lang=json,strict*/"""[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"screenshot","arguments":"{}"}}]},{"role":"tool","content":"file body","tool_call_id":"c1"},{"role":"tool","content":"","tool_call_id":"c2"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c2]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]}]""",
        json);
  }

  [Fact]
  public async Task SendAsync_TwoToolCallsBothWithImages_MergedIntoOneUserMessage()
  {
    string json = await CaptureMessagesAsync(
    [
      new(Role.User, "look", SentAt),
      new(Role.Assistant, "", SentAt,
      [
        new ToolCall("c1", "screenshot", "{}"),
        new ToolCall("c2", "snap", "{}"),
      ]),
      new(Role.Tool, "first", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.ImagePart("image/png", "cA==")]),
      new(Role.Tool, "second", SentAt, ToolCallId: "c2",
          Parts: [new MessagePart.ImagePart("image/jpeg", "anM=")]),
    ]);

    Assert.Equal(
        /*lang=json,strict*/"""[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"screenshot","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"snap","arguments":"{}"}}]},{"role":"tool","content":"first","tool_call_id":"c1"},{"role":"tool","content":"second","tool_call_id":"c2"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c1]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,cA=="}},{"type":"text","text":"[computer screenshot for tool call c2]"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,anM="}}]}]""",
        json);
  }

  [Fact]
  public async Task SendAsync_NoImages_ProducesNoSyntheticUserMessage()
  {
    string json = await CaptureMessagesAsync(
    [
      new(Role.User, "look", SentAt),
      new(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new(Role.Tool, "plain text", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.TextPart("plain text")]),
    ]);

    Assert.Equal(
        /*lang=json,strict*/"""[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":"plain text","tool_call_id":"c1"}]""",
        json);
  }

  private static async Task<string> CaptureMessagesAsync(Message[] messages)
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
    LocalModelProvider provider = new(http,
        new LocalConfiguration(new Uri("http://localhost:1234/v1")) { Retry = new RetryPolicy(1) });
    ModelConfig config = ModelConfig.Create("local-model", null, 128, 0.7f, 4096).Value!;

    Result<ModelResponse> result = await provider.SendAsync(
        config, new ModelRequest(messages), TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.NotNull(capturedBody);
    using JsonDocument doc = JsonDocument.Parse(capturedBody);
    return doc.RootElement.GetProperty("messages").GetRawText();
  }
}
