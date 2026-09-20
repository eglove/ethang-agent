using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Provider.Wire.Tests;

// Spec 5.1/5.3: PartsProjection decides how tool-role parts serialize. None keeps the
// parts array on the tool message (task 4 behavior); PostTurnUser — the z.ai/Local
// projection — flattens tool content to text and merges ALL image parts of the turn's
// tool results into ONE synthetic user message placed after the LAST tool result of
// the assistant tool_calls block, each image preceded by the exact id line
// "[computer screenshot for tool call <toolCallId>]". User-role messages serialize
// parts inline under both projections.
public class PartsProjectionWireTests
{
  private static readonly DateTimeOffset SentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);

  [Fact]
  public void BuildMessages_NoneProjection_KeepsPartsOnToolMessage()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "look", SentAt),
      new Message(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new Message(Role.Tool, "see attached", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ]);

    string json = JsonSerializer.Serialize(
        OpenAiCompatRequestCore.BuildMessages(request), OpenAiCompatRequestCore.WireJson.Options);

    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}],"tool_call_id":"c1"}]""",
        json);
  }

  [Fact]
  public void BuildMessages_PostTurnUser_SingleToolCall_MergesImageIntoOneUserMessage()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "look", SentAt),
      new Message(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new Message(Role.Tool, "see attached", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ]);

    string json = JsonSerializer.Serialize(
        OpenAiCompatRequestCore.BuildMessages(request, PartsProjection.PostTurnUser),
        OpenAiCompatRequestCore.WireJson.Options);

    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":"see attached","tool_call_id":"c1"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c1]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]}]""",
        json);
  }

  [Fact]
  public void BuildMessages_PostTurnUser_TwoToolCalls_SecondHasImage_PlaceAfterLastToolResult()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "look", SentAt),
      new Message(Role.Assistant, "", SentAt,
      [
        new ToolCall("c1", "read", "{}"),
        new ToolCall("c2", "screenshot", "{}"),
      ]),
      new Message(Role.Tool, "file body", SentAt, ToolCallId: "c1"),
      new Message(Role.Tool, "", SentAt, ToolCallId: "c2",
          Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]),
    ]);

    string json = JsonSerializer.Serialize(
        OpenAiCompatRequestCore.BuildMessages(request, PartsProjection.PostTurnUser),
        OpenAiCompatRequestCore.WireJson.Options);

    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"screenshot","arguments":"{}"}}]},{"role":"tool","content":"file body","tool_call_id":"c1"},{"role":"tool","content":"","tool_call_id":"c2"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c2]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,aGVsbG8="}}]}]""",
        json);
  }

  [Fact]
  public void BuildMessages_PostTurnUser_TwoToolCalls_BothWithImages_MergedOneUserMessage()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "look", SentAt),
      new Message(Role.Assistant, "", SentAt,
      [
        new ToolCall("c1", "screenshot", "{}"),
        new ToolCall("c2", "snap", "{}"),
      ]),
      new Message(Role.Tool, "first", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.ImagePart("image/png", "cA==")]),
      new Message(Role.Tool, "second", SentAt, ToolCallId: "c2",
          Parts: [new MessagePart.ImagePart("image/jpeg", "anM=")]),
    ]);

    string json = JsonSerializer.Serialize(
        OpenAiCompatRequestCore.BuildMessages(request, PartsProjection.PostTurnUser),
        OpenAiCompatRequestCore.WireJson.Options);

    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"screenshot","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"snap","arguments":"{}"}}]},{"role":"tool","content":"first","tool_call_id":"c1"},{"role":"tool","content":"second","tool_call_id":"c2"},{"role":"user","content":[{"type":"text","text":"[computer screenshot for tool call c1]"},{"type":"image_url","image_url":{"url":"data:image/png;base64,cA=="}},{"type":"text","text":"[computer screenshot for tool call c2]"},{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,anM="}}]}]""",
        json);
  }

  [Fact]
  public void BuildMessages_PostTurnUser_NoImages_NoSyntheticUserMessage()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "look", SentAt),
      new Message(Role.Assistant, "", SentAt, [new ToolCall("c1", "read", "{}")]),
      new Message(Role.Tool, "plain text", SentAt, ToolCallId: "c1",
          Parts: [new MessagePart.TextPart("plain text")]),
    ]);

    string json = OpenAiCompatRequestBuild(request);

    Assert.Equal(
        """[{"role":"user","content":"look"},{"role":"assistant","content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"read","arguments":"{}"}}]},{"role":"tool","content":"plain text","tool_call_id":"c1"}]""",
        json);
  }

  [Fact]
  public void BuildMessages_PostTurnUser_UserParts_SerializeInline()
  {
    ModelRequest request = new(
    [
      new Message(Role.User, "fallback", SentAt, Parts: [new MessagePart.TextPart("hello")]),
    ]);

    string json = OpenAiCompatRequestBuild(request);

    Assert.Equal(
        """[{"role":"user","content":[{"type":"text","text":"hello"}]}]""",
        json);
  }

  private static string OpenAiCompatRequestBuild(ModelRequest request) =>
      JsonSerializer.Serialize(
          OpenAiCompatRequestCore.BuildMessages(request, PartsProjection.PostTurnUser),
          OpenAiCompatRequestCore.WireJson.Options);
}