using eThangAgent.AgentDomain;

namespace eThangAgent.MemoryDomain;

/// <summary>One listed session's identity line: lineage, size, lifecycle, and tier.
///     <see cref="Tier"/> is always "hot" — every persisted session is fully indexed;
///     cold digests stay deferred behind this seam.</summary>
public sealed record SessionSummary(AgentId Id, string Label, int Depth, int EntryCount, string Status, string Tier);
