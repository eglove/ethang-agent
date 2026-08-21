namespace eThangAgent.ConversationDomain;

public sealed record Message(
    Role Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);
