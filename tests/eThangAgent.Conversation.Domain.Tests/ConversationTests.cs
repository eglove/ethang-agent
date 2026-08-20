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
}
