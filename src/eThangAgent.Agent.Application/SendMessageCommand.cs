using eThangAgent.ConversationDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Send one user turn. <see cref="ImageParts"/> carries optional pasted
///     image parts (issue #20) that ride the user message; null keeps the legacy
///     text-only shape.
///     <para><see cref="UserMessageId"/> is the sender's identity for this input, and it is
///     what makes a resent turn idempotent: a conversation already holding it appends
///     nothing. Null keeps the legacy behaviour exactly - one append, no deduplication.</para></summary>
public sealed record SendMessageCommand(string Text, IReadOnlyList<MessagePart>? ImageParts = null,
    string? UserMessageId = null);
