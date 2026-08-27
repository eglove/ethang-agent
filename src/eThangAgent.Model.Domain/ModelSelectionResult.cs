namespace eThangAgent.ModelDomain;

/// <summary>The final output of the intelligent model selection pipeline.</summary>
public sealed record ModelSelectionResult(
    string ModelId,
    TaskCategory Category,
    ModelFilter AppliedFilter,
    string? Reasoning);
