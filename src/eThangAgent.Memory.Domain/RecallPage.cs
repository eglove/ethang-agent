using eThangAgent.AgentDomain;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// One paged recall result as flattened projection entries — the read model the
/// capability layer renders. <see cref="Hit"/> is nested because the search domain
/// already owns a top-level <c>Hit</c> wrapping a <see cref="MemoryEntry"/>; a recall
/// hit is a distinct projection (entry fields inlined, no entry reference).
/// </summary>
public sealed record RecallPage(IReadOnlyList<RecallPage.Hit> Hits, int TotalMatched, int Page, int Pages)
{
    /// <summary>One matched conversational turn, flattened for rendering.</summary>
    public sealed record Hit(AgentId Session, int Seq, string Role, string Content, DateTimeOffset Timestamp);
}
