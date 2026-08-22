using eThangAgent.AgentDomain;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// A session's lineage row plus its full transcript — the unit that scope and
/// branch resolution filter over before any entry is searched.
/// </summary>
public sealed record SessionCorpus(
    AgentId Id,
    AgentId? ParentId,
    int Depth,
    IReadOnlyList<MemoryEntry> Entries);
