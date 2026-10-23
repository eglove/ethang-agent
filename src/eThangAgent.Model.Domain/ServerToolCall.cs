namespace eThangAgent.ModelDomain;

/// <summary>One server-side tool call the provider executed inside its own response
///     (OpenRouter web_search, web_fetch, image generation). These calls never enter
///     the message history — surfacing them is the transcript's job.</summary>
/// <param name="Tool">Short tool name, e.g. "web_search" (the wire item type without
///     its "_call" suffix or "openrouter:" prefix).</param>
/// <param name="Detail">Human-readable detail when the wire carries one (the search
///     query, the fetched URL), else null.</param>
/// <param name="Sources">Result URLs the wire attached to the call (web_search's
///     action.sources), empty when none. Surfacing shows what the tool actually
///     returned, not just that it ran.</param>
/// <param name="Image">The image an image-generation call produced, when the wire
///     carried a well-formed one; null for every other tool and for a call whose
///     payload was absent or malformed (issue #230: the image used to be dropped).</param>
public sealed record ServerToolCall(string Tool, string? Detail, IReadOnlyList<string>? Sources = null,
    ServerToolCallImage? Image = null)
{
  /// <summary>Never null: an absent list reads as empty.</summary>
  public IReadOnlyList<string> Sources { get; init; } = Sources ?? [];
}
