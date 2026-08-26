namespace eThangAgent.ConversationDomain.Tests;

public class ConversationTests
{
  [Fact]
  public void NewConversation_HasNoMessages()
  {
    Conversation c = new();
    Assert.Empty(c.Messages);
  }

  [Fact]
  public void AddUserMessage_AppendsUserMessage()
  {
    Conversation c = new();
    c.AddUserMessage("Hello");
    _ = Assert.Single(c.Messages);
    Message msg = c.Messages[0];
    Assert.Equal(Role.User, msg.Role);
    Assert.Equal("Hello", msg.Content);
    Assert.NotEqual(default, msg.Timestamp);
  }

  [Fact]
  public void AddAssistantMessage_AppendsAssistantMessage()
  {
    Conversation c = new();
    c.AddAssistantMessage("Hi back");
    _ = Assert.Single(c.Messages);
    Message msg = c.Messages[0];
    Assert.Equal(Role.Assistant, msg.Role);
    Assert.Equal("Hi back", msg.Content);
  }

  [Fact]
  public void Messages_AreTrackedInOrder()
  {
    Conversation c = new();
    c.AddUserMessage("Q1");
    c.AddAssistantMessage("A1");
    c.AddUserMessage("Q2");
    Assert.Equal(3, c.Messages.Count);
    Assert.Equal(Role.User, c.Messages[0].Role);
    Assert.Equal(Role.Assistant, c.Messages[1].Role);
    Assert.Equal(Role.User, c.Messages[2].Role);
    Assert.Equal("Q1", c.Messages[0].Content);
    Assert.Equal("A1", c.Messages[1].Content);
    Assert.Equal("Q2", c.Messages[2].Content);
  }

  [Fact]
  public void Messages_IsReadOnly()
  {
    Conversation c = new();
    c.AddUserMessage("test");
    _ = Assert.IsType<IReadOnlyList<Message>>(c.Messages, exactMatch: false);
  }

  [Fact]
  public void AddToolResult_AppendsToolMessage()
  {
    Conversation conv = new();
    conv.AddToolResult("call_1", "file content here");

    _ = Assert.Single(conv.Messages);
    Message msg = conv.Messages[0];
    Assert.Equal(Role.Tool, msg.Role);
    Assert.Equal("file content here", msg.Content);
    Assert.Equal("call_1", msg.ToolCallId);
    Assert.Null(msg.ToolCalls);
  }

  [Fact]
  public void AddAssistantMessage_WithToolCalls_StoresThem()
  {
    Conversation conv = new();
    List<ToolCall> calls = [new("call_1", "read", /*lang=json,strict*/ "{\"path\":\"f\"}")];
    conv.AddAssistantMessage("", calls);

    Message msg = conv.Messages[0];
    Assert.Equal(Role.Assistant, msg.Role);
    _ = Assert.Single(msg.ToolCalls!);
    Assert.Equal("call_1", msg.ToolCalls![0].Id);
    Assert.Equal("read", msg.ToolCalls[0].Name);
  }

  [Fact]
  public void AddAssistantMessage_WithoutToolCalls_StoresNull()
  {
    Conversation conv = new();
    conv.AddAssistantMessage("hello");

    Message msg = conv.Messages[0];
    Assert.Null(msg.ToolCalls);
    Assert.Null(msg.ToolCallId);
  }

  [Fact]
  public void AddSystemMessage_AppendsSystemMessageInOrder()
  {
    Conversation c = new();
    c.AddUserMessage("q");
    c.AddAssistantMessage("a");
    c.AddSystemMessage("[nudge] consider memories.add");

    Assert.Equal(3, c.Messages.Count);
    Message msg = c.Messages[2];
    Assert.Equal(Role.System, msg.Role);
    Assert.Equal("[nudge] consider memories.add", msg.Content);
    Assert.NotEqual(default, msg.Timestamp);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("\t\n ")]
  public void AddSystemMessage_NullOrWhitespace_Rejects(string? text)
  {
    Conversation c = new();

    // Null throws ArgumentNullException, whitespace ArgumentException — both are ArgumentException.
    _ = Assert.ThrowsAny<ArgumentException>(() => c.AddSystemMessage(text!));

    Assert.Empty(c.Messages);
  }

  [Fact]
  public void AddToolResult_AfterUser_OrderIsPreserved()
  {
    Conversation conv = new();
    conv.AddUserMessage("read file.md");
    List<ToolCall> calls = [new("c1", "read", "{}")];
    conv.AddAssistantMessage(null!, calls);
    conv.AddToolResult("c1", "contents");
    conv.AddAssistantMessage("file.md says hello");

    Assert.Equal(4, conv.Messages.Count);
    Assert.Equal(Role.User, conv.Messages[0].Role);
    Assert.Equal(Role.Assistant, conv.Messages[1].Role);
    Assert.Equal(Role.Tool, conv.Messages[2].Role);
    Assert.Equal(Role.Assistant, conv.Messages[3].Role);
  }
}
