namespace eThangAgent.ConversationDomain.Tests;

public class MessagePartTests
{
  private static MessagePart.ImagePart Image(string mediaType, string base64) => new(mediaType, base64);

  // --- construction happy paths ---

  [Fact]
  public void TextPart_Construction_CarriesText()
  {
    MessagePart.TextPart part = new("hello");

    Assert.Equal("hello", part.Text);
  }

  [Fact]
  public void ImagePart_Construction_CarriesMediaTypeAndData()
  {
    MessagePart.ImagePart part = Image("image/png", "aGVsbG8=");

    Assert.Equal("image/png", part.MediaType);
    Assert.Equal("aGVsbG8=", part.Base64Data);
  }

  // --- ImagePart validation ---

  [Theory]
  [InlineData("image/gif")]
  [InlineData("image/webp")]
  [InlineData("IMAGE/PNG")]
  [InlineData("")]
  [InlineData("text/plain")]
  public void ImagePart_InvalidMediaType_IsRejected(string mediaType)
  {
    ArgumentException thrown = Assert.Throws<ArgumentException>(
        () => Image(mediaType, "aGVsbG8="));

    Assert.Equal("mediaType", thrown.ParamName);
  }

  [Theory]
  [InlineData("")]
  [InlineData("not base64!!")]
  [InlineData("abc")]
  public void ImagePart_InvalidBase64_IsRejected(string base64)
  {
    ArgumentException thrown = Assert.Throws<ArgumentException>(
        () => Image("image/png", base64));

    Assert.Equal("base64Data", thrown.ParamName);
  }

  [Fact]
  public void ImagePart_NullBase64_IsRejected()
  {
    ArgumentException thrown = Assert.Throws<ArgumentNullException>(
        () => Image("image/png", null!));

    Assert.Equal("base64Data", thrown.ParamName);
  }

  // --- Message.Pars carrying parts ---

  [Fact]
  public void Message_WithParts_PreservesParts()
  {
    List<MessagePart> parts = [new MessagePart.TextPart("see attached"), Image("image/png", "aGVsbG8=")];
    Message message = new(Role.User, "prompt", DateTimeOffset.UtcNow, Parts: parts);

    Assert.NotNull(message.Parts);
    _ = Assert.IsType<MessagePart.TextPart>(message.Parts[0]);
    _ = Assert.IsType<MessagePart.ImagePart>(message.Parts[1]);
    Assert.Equal(2, message.Parts.Count);
  }

  [Fact]
  public void Message_WithoutParts_PartsIsNull()
  {
    Message message = new(Role.User, "prompt", DateTimeOffset.UtcNow);

    Assert.Null(message.Parts);
  }

  // --- Conversation.AddToolResult overload ---

  [Fact]
  public void AddToolResult_WithParts_ConstructsToolMessageCarryingParts()
  {
    Conversation conv = new();
    List<MessagePart> parts = [Image("image/jpeg", "aGVsbG8=")];

    conv.AddToolResult("call_1", "screenshot captured", parts);

    _ = Assert.Single(conv.Messages);
    Message msg = conv.Messages[0];
    Assert.Equal(Role.Tool, msg.Role);
    Assert.Equal("call_1", msg.ToolCallId);
    Assert.Equal("screenshot captured", msg.Content);
    Assert.NotNull(msg.Parts);
    _ = Assert.IsType<MessagePart.ImagePart>(msg.Parts[0]);
  }

  [Fact]
  public void AddToolResult_TwoArgOverload_LeavesPartsNull()
  {
    Conversation conv = new();

    conv.AddToolResult("call_1", "plain text");

    Message msg = conv.Messages[0];
    Assert.Equal(Role.Tool, msg.Role);
    Assert.Null(msg.Parts);
  }

  // --- aggregate invariant: parts only on User and Tool ---

  [Fact]
  public void AddUserMessage_WithParts_IsAccepted()
  {
    Conversation conv = new();
    List<MessagePart> parts = [Image("image/png", "aGVsbG8=")];

    conv.AddUserMessage("look at this", parts);

    _ = Assert.Single(conv.Messages);
    Message msg = conv.Messages[0];
    Assert.Equal(Role.User, msg.Role);
    Assert.NotNull(msg.Parts);
  }

  [Fact]
  public void AddAssistantMessage_WithParts_IsRejected()
  {
    Conversation conv = new();
    List<MessagePart> parts = [Image("image/png", "aGVsbG8=")];

    _ = Assert.Throws<ArgumentException>(
        () => conv.AddAssistantMessage("no images here", parts));
    Assert.Empty(conv.Messages);
  }

  [Fact]
  public void AddSystemMessage_WithParts_IsRejected()
  {
    Conversation conv = new();
    List<MessagePart> parts = [Image("image/png", "aGVsbG8=")];

    _ = Assert.Throws<ArgumentException>(
        () => conv.AddSystemMessage("no images here", parts));
    Assert.Empty(conv.Messages);
  }

  [Fact]
  public void AssistantMessage_WithEmptyParts_IsAccepted_NoException()
  {
    Conversation conv = new();

    conv.AddAssistantMessage("text only", []);
    conv.AddSystemMessage("system text", []);

    Assert.Equal(2, conv.Messages.Count);
    Assert.Null(conv.Messages[0].Parts);
    Assert.Null(conv.Messages[1].Parts);
  }
}
