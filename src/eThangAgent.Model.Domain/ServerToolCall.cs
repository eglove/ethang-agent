namespace eThangAgent.ModelDomain;

/// <summary>One server-side tool call the provider executed inside its own response
///     (OpenRouter web_search, web_fetch, image generation). These calls never enter
///     the message history — surfacing them is the transcript's job.</summary>
/// <param name="Tool">Short tool name, e.g. "web_search" (the wire item type without
///     its "_call" suffix).</param>
/// <param name="Detail">Human-readable detail when the wire carries one (the search
///     query, the fetched URL), else null.</param>
public sealed record ServerToolCall(string Tool, string? Detail);
