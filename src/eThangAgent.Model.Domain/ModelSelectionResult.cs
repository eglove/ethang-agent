namespace eThangAgent.ModelDomain;

/// <summary>The output of smart selection: the chosen pair plus a deterministic
/// explanation of why it won.</summary>
public sealed record ModelSelectionResult(string ModelId, string ProviderName, string? Reasoning);
