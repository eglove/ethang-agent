using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire.Tests;

// Exact-JSON pins for message-parts serialization (spec 5.1, task 4): a message with
// non-null non-empty Parts serializes content as a parts ARRAY (text parts as
// {type:text,text:...}; image parts as {type:image_url,image_url:{url:data:<media>;base64,<data>}}),
// in order; Parts null or empty keeps TODAY's flat string content byte-identically.
public class PartsSerializationWireTests
{
  private static readonly DateTimeOffset SentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);

  [Fact]
  public void TranslateMessage_UserTextPart_SerializesPartsArrayContent()
  {
    Message m = new(Role.User, "ignored fallback", SentAt,
        Parts: [new MessagePart.TextPart("hello world")]);

    object translated = OpenAiCompatRequestCore.TranslateMessage(m);

    Assert.Equal(
        """{"role":"user","content":[{"type":"text","text":"hello world"}]}""",
        Serialize(translated));
  }

  [Fact]
  public void TranslateMessage_UserImagePart_SerializesExactDataUrl()
  {
    Message m = new(Role.User, "ignored fallback", SentAt,
        Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]);

    object translated = OpenAiCompatRequestCore.TranslateMessage(m);

    Assert.Equal(
        """{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]}""",
        Serialize(translated));
  }

  [Fact]
  public void TranslateMessage_UserMixedParts_OrderPreserved()
  {
    Message m = new(Role.User, "ignored fallback", SentAt,
        Parts:
        [
          new MessagePart.TextPart("what is this?"),
          new MessagePart.ImagePart("image/jpeg", "anM="),
          new MessagePart.TextPart("be specific"),
        ]);

    object translated = OpenAiCompatRequestCore.TranslateMessage(m);

    Assert.Equal(
        """{"role":"user","content":[{"type":"text","text":"what is this?"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,anM="}},{"type":"text","text":"be specific"}]}""",
        Serialize(translated));
  }

  [Fact]
  public void TranslateMessage_NullParts_KeepsFlatStringContent()
  {
    Message m = new(Role.User, "plain text", SentAt);

    object translated = OpenAiCompatRequestCore.TranslateMessage(m);

    Assert.Equal(
        """{"role":"user","content":"plain text"}""",
        Serialize(translated));
  }

  [Fact]
  public void BuildMessages_NullParts_KeepsFlatStringContent_WholeRequest()
  {
    ModelRequest request = new(
    [
      new Message(Role.System, "sys", SentAt),
      new Message(Role.User, "hello", SentAt),
      new Message(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new Message(Role.Tool, "file contents", SentAt, ToolCallId: "c1"),
    ],
    SystemPrompt: "be brief");

    string json = Serialize(OpenAiCompatRequestCore.BuildMessages(request));

    Assert.Equal(
        """[{"role":"system","content":"be brief"},{"role":"system","content":"sys"},{"role":"user","content":"hello"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":"file contents","tool_call_id":"c1"}]""",
        json);
  }

  private static string Serialize(object payload) =>
      JsonSerializer.Serialize(payload, OpenAiCompatRequestCore.WireJson.Options);
}