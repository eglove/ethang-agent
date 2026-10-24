namespace eThangAgent.ModelDomain;

/// <summary>Token accounting for one provider response, as scored by the provider.
///     CachedInputTokens is the provider-reported prompt-cache hit, when reported.
///     ServerToolCalls is the usage.server_tool_use.web_search_requests count the
///     provider reports for server-executed tools, when reported. ReasoningTokens is
///     the usage.output_tokens_details.reasoning_tokens count - the hidden share of
///     OutputTokens - when reported (issue #262).</summary>
/// <para>Every optional count is NULL when the provider did not report it, never
///     coerced to zero: a provider reporting <c>reasoning_tokens: 0</c> (reasoning
///     billed nothing) states a different fact from one reporting nothing at all, and
///     the measurements these counts feed cannot tell them apart once a fabricated
///     zero is in the record.</para>
public readonly record struct TokenUsage(int InputTokens, int OutputTokens, int? CachedInputTokens = null, int? ServerToolCalls = null, int? ReasoningTokens = null);
