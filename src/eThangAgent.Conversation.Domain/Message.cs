namespace eThangAgent.ConversationDomain;

public sealed record Message(Role Role, string Content, DateTimeOffset Timestamp);
