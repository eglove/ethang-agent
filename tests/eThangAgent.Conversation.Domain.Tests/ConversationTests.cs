namespace eThangAgent.ConversationDomain.Tests;

public class ConversationTests
{
    [Fact]
    public void NewConversation_HasNoMessages()
    {
        var c = new Conversation();
        Assert.Empty(c.Messages);
    }

    [Fact]
    public void AddUserMessage_AppendsUserMessage()
    {
        var c = new Conversation();
        c.AddUserMessage("Hello");
        Assert.Single(c.Messages);
        var msg = c.Messages[0];
        Assert.Equal(Role.User, msg.Role);
        Assert.Equal("Hello", msg.Content);
        Assert.NotEqual(default, msg.Timestamp);
    }

    [Fact]
    public void AddAssistantMessage_AppendsAssistantMessage()
    {
        var c = new Conversation();
        c.AddAssistantMessage("Hi back");
        Assert.Single(c.Messages);
        var msg = c.Messages[0];
        Assert.Equal(Role.Assistant, msg.Role);
        Assert.Equal("Hi back", msg.Content);
    }

    [Fact]
    public void Messages_AreTrackedInOrder()
    {
        var c = new Conversation();
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
        var c = new Conversation();
        c.AddUserMessage("test");
        Assert.IsAssignableFrom<IReadOnlyList<Message>>(c.Messages);
    }

    [Fact]
    public void AddToolResult_AppendsToolMessage()
    {
        var conv = new Conversation();
        conv.AddToolResult("call_1", "file content here");

        Assert.Single(conv.Messages);
        var msg = conv.Messages[0];
        Assert.Equal(Role.Tool, msg.Role);
        Assert.Equal("file content here", msg.Content);
        Assert.Equal("call_1", msg.ToolCallId);
        Assert.Null(msg.ToolCalls);
    }

    [Fact]
    public void AddAssistantMessage_WithToolCalls_StoresThem()
    {
        var conv = new Conversation();
        var calls = new List<ToolCall> { new("call_1", "read", "{\"path\":\"f\"}") };
        conv.AddAssistantMessage("", calls);

        var msg = conv.Messages[0];
        Assert.Equal(Role.Assistant, msg.Role);
        Assert.Single(msg.ToolCalls!);
        Assert.Equal("call_1", msg.ToolCalls![0].Id);
        Assert.Equal("read", msg.ToolCalls[0].Name);
    }

    [Fact]
    public void AddAssistantMessage_WithoutToolCalls_StoresNull()
    {
        var conv = new Conversation();
        conv.AddAssistantMessage("hello");

        var msg = conv.Messages[0];
        Assert.Null(msg.ToolCalls);
        Assert.Null(msg.ToolCallId);
    }

    [Fact]
    public void AddSystemMessage_AppendsSystemMessageInOrder()
    {
        var c = new Conversation();
        c.AddUserMessage("q");
        c.AddAssistantMessage("a");
        c.AddSystemMessage("[nudge] consider memories.add");

        Assert.Equal(3, c.Messages.Count);
        var msg = c.Messages[2];
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
        var c = new Conversation();

        // Null throws ArgumentNullException, whitespace ArgumentException — both are ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => c.AddSystemMessage(text!));

        Assert.Empty(c.Messages);
    }

    [Fact]
    public void AddToolResult_AfterUser_OrderIsPreserved()
    {
        var conv = new Conversation();
        conv.AddUserMessage("read file.md");
        var calls = new List<ToolCall> { new("c1", "read", "{}") };
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
