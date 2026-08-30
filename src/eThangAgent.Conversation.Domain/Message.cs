namespace eThangAgent.ConversationDomain;

/// <summary>One conversation message. <see cref="IsSummary"/> marks a compaction
///     summary: it rides as an ordinary System-role message on the wire (both ACLs
///     serialize it as a system message) and is produced only by Conversation.Compact.</summary>
public sealed record Message(
    Role Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    bool IsSummary = false);
