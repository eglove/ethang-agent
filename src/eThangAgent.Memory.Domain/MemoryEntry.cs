using eThangAgent.AgentDomain;

namespace eThangAgent.MemoryDomain;

/// <summary>One conversational turn as recalled from a persisted transcript.</summary>
public sealed record MemoryEntry(
    AgentId Session,
    int Seq,
    string Role,
    string Content,
    DateTimeOffset Timestamp);
