namespace eThangAgent.ModelDomain;

/// <summary>One observation handed to hosts after a provider call or a compaction:
///     the accounting status plus the estimated bucket breakdown (null before the first
///     usage report).</summary>
public sealed record ContextSnapshot(ContextStatus Status, ContextBreakdown? Breakdown);
