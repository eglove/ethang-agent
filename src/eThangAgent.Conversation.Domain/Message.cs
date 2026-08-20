namespace eThangAgent.Conversation.Domain;

public sealed record Message(Role Role, string Content, DateTimeOffset Timestamp);
