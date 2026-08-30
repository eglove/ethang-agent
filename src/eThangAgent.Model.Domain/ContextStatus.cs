namespace eThangAgent.ModelDomain;

/// <summary>Accounting snapshot for a session: the provider-scored context size (last
///     request's input tokens — the truth), accumulated totals, and utilization against
///     the model's catalog window.</summary>
public sealed record ContextStatus(
    int? LastInputTokens,
    long TotalInputTokens,
    long TotalOutputTokens,
    int? ContextWindow,
    double? UtilizationPercent);
