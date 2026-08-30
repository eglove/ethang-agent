namespace eThangAgent.ModelDomain;

/// <summary>Token accounting for one provider response, as scored by the provider.
///     CachedInputTokens is the provider-reported prompt-cache hit, when reported.</summary>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens, int? CachedInputTokens = null);
