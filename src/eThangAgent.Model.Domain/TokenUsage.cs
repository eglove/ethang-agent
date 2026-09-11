namespace eThangAgent.ModelDomain;

/// <summary>Token accounting for one provider response, as scored by the provider.
///     CachedInputTokens is the provider-reported prompt-cache hit, when reported.
///     ServerToolCalls is the usage.server_tool_use.web_search_requests count the
///     provider reports for server-executed tools, when reported.</summary>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens, int? CachedInputTokens = null, int? ServerToolCalls = null);
