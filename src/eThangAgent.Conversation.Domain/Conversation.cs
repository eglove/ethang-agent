namespace eThangAgent.ConversationDomain;

public class Conversation
{
    private readonly List<Message> _messages = [];

    public IReadOnlyList<Message> Messages => _messages.AsReadOnly();

    public void AddUserMessage(string text)
        => _messages.Add(new Message(Role.User, text, DateTimeOffset.UtcNow));

    public void AddAssistantMessage(string text)
        => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow));

    public void AddAssistantMessage(string text, IReadOnlyList<ToolCall>? toolCalls)
        => _messages.Add(new Message(Role.Assistant, text, DateTimeOffset.UtcNow, toolCalls));

    public void AddToolResult(string toolCallId, string content)
        => _messages.Add(new Message(Role.Tool, content, DateTimeOffset.UtcNow, ToolCallId: toolCallId));
}
