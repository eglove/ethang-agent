namespace eThangAgent.AgentDomain;

/// <summary>One matched conversational turn, flattened for rendering. Top-level because
///     the search domain already owns a top-level hit wrapping its own entry; a recall
///     hit is a distinct projection (entry fields inlined, no entry reference).</summary>
public sealed record RecallHit(AgentId Session, int Seq, string Role, string Content, DateTimeOffset Timestamp);

/// <summary>One paged recall result as flattened projection entries — the read model the
///     capability layer renders.</summary>
public sealed record RecallPage(IReadOnlyList<RecallHit> Hits, int TotalMatched, int Page, int Pages);
