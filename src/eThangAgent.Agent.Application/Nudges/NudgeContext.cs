namespace eThangAgent.Agent.Application.Nudges;

/// <summary>Facts about a completed turn that a nudge policy may condition on.</summary>
/// <param name="TurnNumber">1-based ordinal of the handler invocation being evaluated.</param>
/// <param name="LastToolCalls">Tool calls executed during the turn just completed.</param>
/// <param name="MemoriesWrittenTotal">Curated memories written this session so far.</param>
public sealed record NudgeContext(int TurnNumber, int LastToolCalls, int MemoriesWrittenTotal);
