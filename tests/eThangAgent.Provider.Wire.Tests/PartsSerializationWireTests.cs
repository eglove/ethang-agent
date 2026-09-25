using System.Text.Encodings.Web;
using System.Text.Json;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Provider.Wire.Tests;

// Exact-JSON pins for the Responses-API input-item serialization (ResponsesApiRequestCore
// .BuildInput / .TranslateTool): conversation messages translate to typed input items
// (message / function_call / function_call_output), tool results carry image parts
// NATIVELY inside the function_call_output part array — the chat-completions
// image-projection workaround has no equivalent here — and tools translate to the FLAT
// function shape (no nested "function" object; "items" only on array parameters).
// The system prompt NEVER appears in BuildInput output: it travels as the body's
// "instructions" key, which the provider adds.
public class PartsSerializationWireTests
{
  private static readonly DateTimeOffset SentAt = new(2026, 1, 15, 8, 30, 5, TimeSpan.Zero);

  private static readonly JsonSerializerOptions WireJson = new()
  {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };

  [Fact]
  public void BuildInput_UserMessageWithoutParts_SerializesSingleInputTextItem()
  {
    ModelRequest request = new([new Message(Role.User, "hello", SentAt)]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}]""",
        json);
  }

  [Fact]
  public void BuildInput_UserMessageWithParts_SerializesInputTextAndInputImageParts()
  {
    ModelRequest request = new(
        [
          new Message(Role.User, "ignored fallback", SentAt,
              Parts:
              [
                new MessagePart.TextPart("what is this?"),
                new MessagePart.ImagePart("image/png", "aGVsbG8="),
              ]),
        ]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"user","content":[{"type":"input_text","text":"what is this?"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}]""",
        json);
  }

  [Fact]
  public void BuildInput_SystemRoleHistoryMessage_SerializesSystemMessageItem()
  {
    ModelRequest request = new(
        [
          new Message(Role.System, "compaction summary", SentAt),
          new Message(Role.User, "hello", SentAt),
        ]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"system","content":[{"type":"input_text","text":"compaction summary"}]},{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}]""",
        json);
  }

  [Fact]
  public void BuildInput_AssistantTextMessage_SerializesOutputTextItem()
  {
    ModelRequest request = new([new Message(Role.Assistant, "here is the answer", SentAt)]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"here is the answer"}]}]""",
        json);
  }

  [Fact]
  public void BuildInput_AssistantTextAndToolCalls_SerializesTextItemThenFunctionCallItems()
  {
    ModelRequest request = new(
        [
          new Message(Role.Assistant, "let me check", SentAt, [new ToolCall("c1", "read", "{}")]),
        ]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"let me check"}]},{"type":"function_call","call_id":"c1","name":"read","arguments":"{}"}]""",
        json);
  }

  [Fact]
  public void BuildInput_TextlessAssistantToolCalls_SerializesFunctionCallItemsOnly()
  {
    // JSON002 fires only in the format/IDE host; the pragma pair mirrors the repo's
    // standing pattern for literal JSON arguments in fixtures.
#pragma warning disable JSON002
    ModelRequest request = new(
        [
          new Message(Role.Assistant, "", SentAt,
              [new ToolCall("c1", "read", "{}"), new ToolCall("c2", "exec", "{\"cmd\":\"ls\"}")]),
        ]);
#pragma warning restore JSON002

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"function_call","call_id":"c1","name":"read","arguments":"{}"},{"type":"function_call","call_id":"c2","name":"exec","arguments":"{\"cmd\":\"ls\"}"}]""",
        json);
  }

  [Fact]
  public void BuildInput_ToolResultTextOnly_SerializesFlatStringOutput()
  {
    ModelRequest request = new(
        [
          new Message(Role.Tool, "file contents", SentAt, ToolCallId: "c1"),
        ]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"function_call_output","call_id":"c1","output":"file contents"}]""",
        json);
  }

  [Fact]
  public void BuildInput_ToolResultWithImage_SerializesPartsArrayOutput()
  {
    ModelRequest request = new(
        [
          new Message(Role.Tool, "screenshot saved", SentAt, ToolCallId: "c1",
              Parts:
              [
                new MessagePart.TextPart("screenshot saved"),
                new MessagePart.ImagePart("image/png", "aGVsbG8="),
              ]),
        ]);

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.Equal(
        /*lang=json,strict*/"""[{"type":"function_call_output","call_id":"c1","output":[{"type":"input_text","text":"screenshot saved"},{"type":"input_image","image_url":"data:image/png;base64,aGVsbG8="}]}]""",
        json);
  }

  [Fact]
  public void BuildInput_ToolResultWithoutCallId_ThrowsArgumentException()
  {
    ModelRequest request = new([new Message(Role.Tool, "orphan result", SentAt)]);

    _ = Assert.Throws<ArgumentException>(
        () => ResponsesApiRequestCore.BuildInput(request));
  }

  [Fact]
  public void TranslateTool_TextParameter_SerializesFlatFunctionShapeWithoutItems()
  {
    ToolDefinition tool = new(
        "read", "Read a file.", [new ToolParameter("path", ToolParameterType.Text, "The path.")]);

    string json = Serialize(ResponsesApiRequestCore.TranslateTool(tool));

    Assert.Equal(
        /*lang=json,strict*/"""{"type":"function","name":"read","description":"Read a file.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"The path."}},"required":["path"],"additionalProperties":false}}""",
        json);
  }

  [Fact]
  public void TranslateTool_TextArrayParameter_EmitsItemsKey()
  {
    ToolDefinition tool = new(
        "list",
        "List files.",
        [new ToolParameter("patterns", ToolParameterType.TextArray, "The patterns.")]);

    string json = Serialize(ResponsesApiRequestCore.TranslateTool(tool));

    Assert.Equal(
        /*lang=json,strict*/"""{"type":"function","name":"list","description":"List files.","parameters":{"type":"object","properties":{"patterns":{"type":"array","description":"The patterns.","items":{"type":"string"}}},"required":["patterns"],"additionalProperties":false}}""",
        json);
  }

  [Fact]
  public void TranslateTool_WholeNumberParameterWithMinimum_EmitsMinimum()
  {
    ToolDefinition tool = new(
        "wait",
        "Wait.",
        [new ToolParameter("seconds", ToolParameterType.WholeNumber, "How long.", Minimum: 3)]);

    string json = Serialize(ResponsesApiRequestCore.TranslateTool(tool));

    Assert.Equal(
        /*lang=json,strict*/"""{"type":"function","name":"wait","description":"Wait.","parameters":{"type":"object","properties":{"seconds":{"type":"integer","description":"How long.","minimum":3}},"required":["seconds"],"additionalProperties":false}}""",
        json);
  }

  [Fact]
  public void BuildInput_SystemPrompt_DoesNotAppearInInputItems()
  {
    ModelRequest request = new(
        [new Message(Role.User, "hello", SentAt)],
        SystemPrompt: "TOP-SECRET-INSTRUCTIONS-MARKER");

    string json = Serialize(ResponsesApiRequestCore.BuildInput(request));

    Assert.DoesNotContain("TOP-SECRET-INSTRUCTIONS-MARKER", json, StringComparison.Ordinal);
    Assert.Contains("hello", json, StringComparison.Ordinal);
  }

  private static string Serialize(object payload) =>
      JsonSerializer.Serialize(payload, WireJson);
}
