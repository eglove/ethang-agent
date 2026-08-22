namespace eThangAgent.AgentDomain;

/// <summary>One paged recall result as flattened projection entries — the read model the
///     capability layer renders. Hit is nested because the search domain already owns a
///     top-level hit wrapping its own entry; a recall hit is a distinct projection (entry
///     fields inlined, no entry reference).</summary>
public sealed record RecallPage(IReadOnlyList<RecallPage.Hit> Hits, int TotalMatched, int Page, int Pages)
{
    /// <summary>One matched conversational turn, flattened for rendering.</summary>
    public sealed record Hit(AgentId Session, int Seq, string Role, string Content, DateTimeOffset Timestamp);
}
