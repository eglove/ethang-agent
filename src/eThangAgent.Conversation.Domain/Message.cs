namespace eThangAgent.ConversationDomain;

/// <summary>One conversation message. <see cref="IsSummary"/> marks a compaction
///     summary: it rides as an ordinary System-role message on the wire (both ACLs
///     serialize it as a system message) and is produced only by Conversation.Compact.
///     <para><see cref="UserMessageId"/> is the sender's identity for a user message, and it
///     is what makes a RESENT turn idempotent: a host that hydrates a conversation already
///     holding this identity appends nothing. It is null for every message that did not
///     arrive with one, which is every assistant, tool and summary message and every user
///     message from a sender that names no identity.</para></summary>
public sealed record Message(
    Role Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    bool IsSummary = false,
    IReadOnlyList<MessagePart>? Parts = null,
    string? UserMessageId = null);
