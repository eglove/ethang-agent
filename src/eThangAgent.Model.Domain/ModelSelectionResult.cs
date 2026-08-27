namespace eThangAgent.ModelDomain;

/// <summary>The final output of the intelligent model selection pipeline: a chosen
/// model+provider pair plus the task category and applied filter.</summary>
public sealed record ModelSelectionResult(
    string ModelId,
    string ProviderName,
    TaskCategory Category,
    ModelFilter AppliedFilter,
    string? Reasoning);