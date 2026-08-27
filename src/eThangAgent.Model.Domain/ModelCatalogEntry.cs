namespace eThangAgent.ModelDomain;

/// <summary>A slimmed view of one OpenRouter model: effective prices, context length, capabilities, and quality score.</summary>
public sealed record ModelCatalogEntry(
    string Id,
    decimal PromptPricePerToken,
    decimal CompletionPricePerToken,
    int ContextLength,
    bool SupportsToolUse,
    bool SupportsVision,
    double? QualityScore,
    string? Description);
