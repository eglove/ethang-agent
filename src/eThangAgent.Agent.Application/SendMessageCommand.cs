using eThangAgent.ConversationDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Send one user turn. <see cref="ImageParts"/> carries optional pasted
///     image parts (issue #20) that ride the user message; null keeps the legacy
///     text-only shape.</summary>
public sealed record SendMessageCommand(string Text, IReadOnlyList<MessagePart>? ImageParts = null);
