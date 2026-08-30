namespace eThangAgent.ModelDomain;

/// <summary>Character sizes of one request's three cost buckets, as built by the agent
///     loop. Scaled against the provider's input-token score, these produce the statusline
///     breakdown estimate (4 characters ≈ 1 token; estimates size evictions and UI only —
///     provider usage reports are the truth used for decisions).</summary>
public sealed record ContextComposition(int SystemPromptChars, long MessageChars, long ToolDefinitionChars);
